using System.Collections.ObjectModel;

namespace StagePlayout.Core.Models;

public class Playlist
{
    public ObservableCollection<Cue> Cues { get; } = new();

    public int CurrentIndex { get; private set; } = -1;

    public Cue? Current =>
        CurrentIndex >= 0 && CurrentIndex < Cues.Count ? Cues[CurrentIndex] : null;

    public event EventHandler? CurrentChanged;

    /// <summary>offset 0 = atual, 1 = seguinte, etc.</summary>
    public Cue? PeekNext(int offset)
    {
        var idx = CurrentIndex + offset;
        return idx >= 0 && idx < Cues.Count ? Cues[idx] : null;
    }

    /// <summary>Botão GO (barra de espaço): avança para o cue seguinte.</summary>
    public Cue? Go()
    {
        if (Cues.Count == 0) return null;
        CurrentIndex = Math.Min(CurrentIndex + 1, Cues.Count - 1);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    /// <summary>Volta ao cue anterior.</summary>
    public Cue? Previous()
    {
        if (Cues.Count == 0) return null;
        CurrentIndex = CurrentIndex <= 0 ? 0 : CurrentIndex - 1;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    public Cue? Select(int index)
    {
        if (index < 0 || index >= Cues.Count) return null;
        CurrentIndex = index;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    /// <summary>
    /// Move um cue para antes/depois de outro. Grupos movem o bloco inteiro
    /// (cabeçalho + filhos contíguos). Um cue largado sobre um filho de um
    /// grupo passa a pertencer a esse grupo; largado em top-level sai do grupo.
    /// </summary>
    public void Move(Cue cue, Cue? target, bool insertAfter = false)
    {
        if (!Cues.Contains(cue)) return;
        if (ReferenceEquals(cue, target)) return;

        // bloco a mover
        var block = new List<Cue> { cue };
        if (cue.IsGroup)
        {
            var start = Cues.IndexOf(cue);
            for (var i = start + 1; i < Cues.Count && Cues[i].ParentId == cue.Id; i++)
                block.Add(Cues[i]);
        }
        if (target is not null && block.Contains(target)) return; // alvo dentro do bloco

        var oldIndexes = block.Select(c => Cues.IndexOf(c)).ToList();
        var current = Current;

        int targetIndex;
        if (target is null)
        {
            targetIndex = Cues.Count;
        }
        else
        {
            var ti = Cues.IndexOf(target);
            if (ti < 0) return;
            targetIndex = insertAfter ? ti + 1 : ti;
        }

        // um cue (não-grupo) adota o grupo do alvo onde é largado
        if (!cue.IsGroup)
            cue.ParentId = target?.ParentId;

        foreach (var c in block) Cues.Remove(c);
        targetIndex -= oldIndexes.Count(i => i < targetIndex);
        targetIndex = Math.Clamp(targetIndex, 0, Cues.Count);

        for (var i = 0; i < block.Count; i++)
            Cues.Insert(targetIndex + i, block[i]);

        if (current is not null)
            CurrentIndex = Cues.IndexOf(current);

        RefreshChildCounts();
    }

    // ===== Grupos / playlists =====

    /// <summary>
    /// Cria um grupo com os cues dados (ficam em cadeia auto-continuar;
    /// o último mantém o seu comportamento de fim, que pára a sequência).
    /// </summary>
    public Cue GroupSelection(string name, IReadOnlyList<Cue> members)
    {
        var first = members.OrderBy(c => Cues.IndexOf(c)).First();
        var group = new Cue
        {
            Name = name,
            FilePath = "",
            IsGroup = true,
            IsExpanded = true,
        };
        Cues.Insert(Cues.IndexOf(first), group);

        foreach (var m in members)
            m.ParentId = group.Id;

        var children = Cues.Where(c => c.ParentId == group.Id).ToList();
        for (var i = 0; i < children.Count - 1; i++)
            children[i].End = CueEnd.AutoContinue;

        group.ChildCount = children.Count;
        return group;
    }

    /// <summary>Desfaz o grupo; os clips ficam como cues top-level.</summary>
    public void Ungroup(Cue group)
    {
        foreach (var c in Cues.Where(c => c.ParentId == group.Id).ToList())
            c.ParentId = null;
        Remove(group);
        RefreshChildCounts();
    }

    public void RefreshChildCounts()
    {
        foreach (var g in Cues.Where(c => c.IsGroup))
            g.ChildCount = Cues.Count(c => c.ParentId == g.Id);
    }

    public void Add(Cue cue) => Cues.Add(cue);

    public void Remove(Cue cue)
    {
        var idx = Cues.IndexOf(cue);
        Cues.Remove(cue);
        if (idx >= 0 && idx <= CurrentIndex)
            CurrentIndex = Math.Max(-1, CurrentIndex - 1);
    }
}
