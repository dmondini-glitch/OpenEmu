# Como gerar o pacote do OpenEmu para Windows

Este guia cobre as três arquiteturas suportadas: **x64**, **x86 (32-bit)** e **ARM64**.
Todos os comandos rodam a partir da pasta `Windows/` do repositório.

## Pré-requisitos

| Item | Observação |
|---|---|
| .NET SDK 9 | https://dot.net — `dotnet --version` deve mostrar 9.x |
| PowerShell 7 (`pwsh`) ou Windows PowerShell 5.1 | para `scripts\build-windows.ps1` |
| Internet | os cores são baixados de `https://buildbot.libretro.com` durante o empacotamento |
| Opcional: macOS/Linux | `scripts/build-windows.sh` faz cross-build (o .NET publica para `win-*` de qualquer SO); precisa de `curl`, `unzip`, `zip`, `python3` |

Nenhum Visual Studio é necessário. O build não precisa de código nativo próprio: o app é .NET puro e os
cores libretro são binários prontos do buildbot.

## Comando único

```powershell
cd Windows
.\scripts\build-windows.ps1 -Arch x64      # ou -Arch x86 / -Arch arm64
```

Saída:

```
dist\OpenEmu-Windows-<arch>\        pasta pronta para distribuir (portátil, sem instalador)
dist\OpenEmu-Windows-<arch>.zip     a mesma pasta compactada
```

No macOS/Linux:

```bash
cd Windows
scripts/build-windows.sh x64          # x86 | arm64
```

Parâmetros úteis:

| Parâmetro | Efeito |
|---|---|
| `-SkipCores` / `SKIP_CORES=1` | não baixa os cores (pacote só com o app, ~100 MB) |
| `-IncludeX86Cores` / `INCLUDE_X86_CORES=1` | só ARM64: inclui também os cores x86 para Windows 10 ARM (ver abaixo) |
| `-Configuration Debug` | build de depuração |

## O que vai dentro do pacote

```
OpenEmu-Windows-<arch>\
├─ OpenEmu.exe                     app (self-contained: não precisa instalar o .NET)
├─ cli\openemu-cli.exe             front-end headless (scripts, diagnóstico)
├─ host\<arch>\OpenEmu.CoreHost.exe processo auxiliar que roda o core fora do app
├─ cores\windows-x64\*.dll         cores libretro (um por console) — ou windows-x86
├─ README.md
└─ EMPACOTAMENTO-WINDOWS.md
```

### Por arquitetura

| Pacote | App | Core host | Cores incluídos | Como os cores rodam |
|---|---|---|---|---|
| **x64** | nativo x64 | `host\x64` (opcional) | `cores\windows-x64` (61 cores) | no processo do app (ou no host, se ativado em Preferências → Jogo) |
| **x86** | nativo x86 | `host\x86` (opcional) | `cores\windows-x86` (57 cores; sem Dolphin/GameCube, PCSX2, DeSmuME, Kronos, melonDS DS) | no processo do app |
| **ARM64** | nativo ARM64 | `host\x64` **e** `host\x86` | `cores\windows-x64` (+ `windows-x86` com `-IncludeX86Cores`) | **sempre no core host**, sob a emulação x64/x86 do Windows |

Por que ARM64 usa um processo auxiliar: o buildbot libretro não publica cores nativos para Windows ARM64, e um
processo ARM64 não pode carregar DLLs x64/x86. O `OpenEmu.CoreHost.exe` x64 roda sob a camada de emulação do
Windows 11 ARM (ou x86 no Windows 10 ARM), e o app conversa com ele por memória compartilhada (vídeo, teclado)
e named pipe (comandos). É a mesma arquitetura de "helper por jogo" do OpenEmu original. Cores OpenGL
(N64, PSX HW, PSP, Dreamcast, DS, GameCube, PS2) não funcionam nesse modo; o app oferece automaticamente o core
por software do mesmo sistema quando existe.

Windows 10 ARM só emula x86: gere o pacote com `-IncludeX86Cores` e defina a variável de ambiente
`OPENEMU_CORE_PLATFORM=windows-x86` antes de abrir o app (ou crie um atalho com ela).

## Passo a passo manual (sem o script)

```powershell
# 1) app e CLI
dotnet publish src\OpenEmu.App\OpenEmu.App.csproj -c Release -r win-x64 --self-contained true -o dist\x64
dotnet publish src\OpenEmu.Cli\OpenEmu.Cli.csproj -c Release -r win-x64 --self-contained true -o dist\x64\cli

# 2) core host (para ARM64 publique win-x64 E win-x86)
dotnet publish src\OpenEmu.CoreHost\OpenEmu.CoreHost.csproj -c Release -r win-x64 --self-contained true -o dist\x64\host\x64

# 3) cores
.\scripts\download-cores.ps1 -OutDir dist\x64\cores -Platform windows-x64

# 4) zip
Compress-Archive -Path dist\x64\* -DestinationPath dist\OpenEmu-Windows-x64.zip
```

RIDs válidos: `win-x64`, `win-x86`, `win-arm64`.

## Verificação do pacote

```powershell
cd dist\OpenEmu-Windows-x64
.\cli\openemu-cli.exe platform                              # arquitetura, plataforma dos cores, core host
.\cli\openemu-cli.exe cores                                 # todos devem aparecer como "installed"
.\cli\openemu-cli.exe run --core fceumm --rom jogo.nes --frames 300 --screenshot out.png --state
.\cli\openemu-cli.exe run --core 2048 --frames 120 --host   # mesmo teste passando pelo core host
```

No ARM64 o `run` usa o core host automaticamente.

## CI (GitHub Actions)

`.github/workflows/windows.yml` roda em cada push no branch `windows`:

1. `test` — build + 25 testes xUnit + smoke test de core real (em processo e via core host);
2. `test-x86` — publica a CLI 32-bit e roda um core x86 de verdade;
3. `package` — matriz x64 / x86 / arm64 com `build-windows.ps1`, publicando `OpenEmu-Windows-<arch>.zip` como artefato.

## Onde o app grava dados

| Caminho | Conteúdo |
|---|---|
| `%APPDATA%\OpenEmu\settings.json` | preferências, bindings, cores padrão, opções de core |
| `%APPDATA%\OpenEmu\Cores\<plataforma>\` | cores instalados/atualizados pelo gerenciador (têm prioridade sobre `cores\` do pacote) |
| `%APPDATA%\OpenEmu\openvgdb.sqlite` | banco de títulos/capas (Preferências → Biblioteca → Baixar) |
| `Documentos\OpenEmu Library\` | `roms\`, `Battery Saves\`, `Save States\`, `Screenshots\`, `Artwork\`, `BIOS\`, `Library.sqlite` |

Variáveis de ambiente: `OPENEMU_HOME`, `OPENEMU_LIBRARY`, `OPENEMU_CORES`, `OPENEMU_CORE_PLATFORM`, `OPENEMU_COREHOST`.

## Problemas comuns

| Sintoma | Causa / solução |
|---|---|
| `download-cores.ps1` falha em alguns cores | buildbot temporariamente fora; rode de novo (os já baixados são mantidos) |
| "core host … não foi encontrado" no ARM64 | o pacote foi gerado sem `host\x64`; use o script `build-windows.ps1 -Arch arm64` |
| Jogo abre e fecha em ARM64 com core OpenGL | escolha um core por software (ex.: `mednafen_psx` em vez de `mednafen_psx_hw`) |
| Sem áudio | o áudio usa WASAPI no processo que roda o core (app ou core host); verifique o dispositivo padrão do Windows |
| Windows SmartScreen | o pacote não é assinado; "Mais informações → Executar assim mesmo", ou assine `OpenEmu.exe` com `signtool` |
