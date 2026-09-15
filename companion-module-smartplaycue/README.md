# companion-module-smartplaycue

Módulo oficial do **Smart Play Cue** para Bitfocus Companion / Stream Deck (OSC).

## Estrutura

- `main.js` — entrypoint do módulo (ações, feedbacks, variáveis, presets, config)
- `presets.js` — presets de botões (transporte, layers, tempo)
- `companion/manifest.json` — manifesto oficial do Companion (obrigatório desde v3.0)
- `companion/HELP.md` — ajuda mostrada na UI do Companion

## Desenvolvimento

```bash
npm install
npm pack                    # gera smartplaycue-<versao>.tgz
```

No Companion: definir o "Developer modules path" para a pasta que contém este
módulo e (re)iniciar as ligações.

## Empacotamento oficial

A via oficial usa as ferramentas do ecossistema:

```bash
yarn install
yarn companion-module-build   # valida manifest + licenças e gera o .tgz
yarn companion-module-check   # validação apenas
```

## Licença

Este módulo é MIT (requisito do ecossistema Companion — o source de módulos
oficiais é sempre MIT). A aplicação Smart Play Cue em si é proprietária
(All Rights Reserved, ver LICENSE na raiz do repo) — são artefactos distintos.
