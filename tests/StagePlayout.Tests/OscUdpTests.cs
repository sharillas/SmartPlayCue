using StagePlayout.Core.Services;

namespace StagePlayout.Tests;

public class OscUdpTests
{
    [Fact]
    public void Pack_Unpack_Int()
    {
        var m = OscUdp.Unpack(OscUdp.Pack("/stageplayout/cue", 3)).Single();
        Assert.Equal("/stageplayout/cue", m.Address);
        Assert.Single(m.Args);
        Assert.Equal(3, Assert.IsType<int>(m.Args[0]));
    }

    [Fact]
    public void Pack_Unpack_NoArgs_Has_Empty_TypeTag()
    {
        var data = OscUdp.Pack("/stageplayout/go");
        var m = OscUdp.Unpack(data).Single();
        Assert.Equal("/stageplayout/go", m.Address);
        Assert.Empty(m.Args);
    }

    [Fact]
    public void Address_Multiple_Of_Four_Is_Terminated()
    {
        // "/stageplayout/go" tem 16 chars (múltiplo de 4) — exige \0 terminador
        var data = OscUdp.Pack("/stageplayout/go");
        Assert.Equal((byte)'/', data[0]);
        Assert.Equal((byte)0, data[16]); // terminador logo após o endereço
    }

    [Fact]
    public void Pack_Unpack_Float_And_String()
    {
        var m = OscUdp.Unpack(OscUdp.Pack("/x", 0.75f, "ON AIR")).Single();
        Assert.Equal(2, m.Args.Length);
        Assert.Equal(0.75f, Assert.IsType<float>(m.Args[0]), 3);
        Assert.Equal("ON AIR", Assert.IsType<string>(m.Args[1]));
    }

    [Fact]
    public void Pack_Unpack_Bool()
    {
        var m = OscUdp.Unpack(OscUdp.Pack("/x", true)).Single();
        Assert.True(Assert.IsType<bool>(m.Args[0]));

        var m2 = OscUdp.Unpack(OscUdp.Pack("/x", false)).Single();
        Assert.False(Assert.IsType<bool>(m2.Args[0]));
    }

    [Fact]
    public void Pack_Unpack_Int_Is_BigEndian()
    {
        var data = OscUdp.Pack("/x", 258);
        // arg começa após: endereço "/x" (1 + 3 pad = 4) + type tag ",i" (2 + 2 pad = 4)
        Assert.Equal(new byte[] { 0, 0, 1, 2 }, data.Skip(8).Take(4));
    }

    [Fact]
    public void Unpack_Bundle_Two_Messages()
    {
        var e1 = OscUdp.Pack("/a", 1);
        var e2 = OscUdp.Pack("/b", "hello");
        var bundle = new List<byte>();
        bundle.AddRange(System.Text.Encoding.ASCII.GetBytes("#bundle\0"));
        bundle.AddRange(new byte[8]); // timetag
        void AddSize(byte[] d)
        {
            bundle.Add((byte)(d.Length >> 24));
            bundle.Add((byte)(d.Length >> 16));
            bundle.Add((byte)(d.Length >> 8));
            bundle.Add((byte)d.Length);
        }
        AddSize(e1); bundle.AddRange(e1);
        AddSize(e2); bundle.AddRange(e2);

        var msgs = OscUdp.Unpack(bundle.ToArray()).ToList();
        Assert.Equal(2, msgs.Count);
        Assert.Equal("/a", msgs[0].Address);
        Assert.Equal(1, Assert.IsType<int>(msgs[0].Args[0]));
        Assert.Equal("/b", msgs[1].Address);
        Assert.Equal("hello", Assert.IsType<string>(msgs[1].Args[0]));
    }

    [Fact]
    public void Unpack_Message_Without_TypeTag_Yields_No_Args()
    {
        // senders antigos/imperfeitos podem omitir a type tag
        var data = new List<byte>();
        data.AddRange(System.Text.Encoding.UTF8.GetBytes("/smartcue/status"));
        for (var i = 0; i < 4 - ("/smartcue/status".Length % 4); i++) data.Add(0);

        var m = OscUdp.Unpack(data.ToArray()).Single();
        Assert.Equal("/smartcue/status", m.Address);
        Assert.Empty(m.Args);
    }

    [Fact]
    public void Unpack_Garbage_Does_Not_Throw()
    {
        var msgs = OscUdp.Unpack(new byte[] { 1, 2, 3, 4 }).ToList();
        Assert.Empty(msgs);
    }
}
