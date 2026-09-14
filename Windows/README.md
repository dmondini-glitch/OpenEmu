# OpenEmu for Windows

Port do [OpenEmu](https://github.com/OpenEmu/OpenEmu) (macOS) para **Windows 10/11 — x64, x86 (32-bit) e ARM64**,
mantido neste fork em `Windows/`. Reproduz a experiência do OpenEmu — biblioteca de jogos com capas, consoles na barra lateral,
coleções, homebrew, save states, cheats, controles por sistema — com a emulação fornecida por **cores libretro**,
um plugin ("driver") por console, já mapeados para **todos os 42 sistemas** do OpenEmu.

*English summary at the bottom.*

## Recursos (paridade com o OpenEmu)

| Área | O que existe |
|---|---|
| Biblioteca | Importação por arrastar/soltar, arquivos ou pastas; detecção automática do sistema (extensão + assinatura); hash MD5/CRC/SHA1 como o OpenEmu (cabeçalhos iNES/SNES/Lynx/A78 ignorados); títulos e capas via **OpenVGDB**; ZIP com um ROM é extraído; cópia opcional dos ROMs para a biblioteca |
| Navegação | Barra lateral **Consoles / Coleções / Homebrew**, grade com capas (tamanho ajustável) ou lista, busca, coleções manuais, "Adicionados/Jogados recentemente", capas manuais, informações, contagem de partidas |
| Jogo | Janela por jogo com barra HUD (pausa, reset, avanço rápido, save/load, captura, cheats, opções do core, controles, disco, volume, tela cheia), escala inteira/proporção, filtro nearest/linear, mensagens do core, pausa ao perder foco |
| Save states | Slots rápidos 1–9, estados nomeados com miniatura PNG, auto-save ao sair / retomar ao abrir, gerenciador (carregar/renomear/excluir); SRAM/RTC persistidos automaticamente |
| Cheats | Por jogo (Game Genie / PAR / raw), ativados ao vivo via `retro_cheat_set` |
| Controles | Mapas de teclado padrão **idênticos aos do OpenEmu** (extraídos dos plugins de sistema), gamepads XInput com layout RetroPad, rebinding por sistema/jogador na tela de Preferências, hotkeys configuráveis, teclado passado ao core em computadores (C64/MSX/Atari 8-bit) |
| Cores | Gerenciador de cores: instalar/atualizar/remover a partir do buildbot libretro, core padrão por sistema, opções do core persistidas; o pacote de distribuição já traz **todos os cores** (`cores\`) |
| BIOS | Lista de firmwares exigidos/opcionais por sistema (do libretro-core-info), importação por arquivo ou arrastar |
| Homebrew | Mesmo catálogo curado do OpenEmu (`games.xml`), download e execução direta |
| Idiomas | Inglês e Português (Brasil) |
| Extras | CLI headless (`openemu-cli`), abertura de ROM por linha de comando / "Abrir com", cores OpenGL (N64, PSX HW, PSP, Dreamcast, DS, GameCube, PS2, Saturn) via `GlGameView` |

## Arquiteturas

| Pacote | App | Cores libretro | Como rodam |
|---|---|---|---|
| `OpenEmu-Windows-x64.zip` | nativo x64 | `cores\windows-x64` (61) | dentro do processo do app (ou no core host, opcional) |
| `OpenEmu-Windows-x86.zip` | nativo x86 | `cores\windows-x86` (57 — sem Dolphin/GameCube, PCSX2, DeSmuME, Kronos, melonDS DS) | dentro do processo do app |
| `OpenEmu-Windows-arm64.zip` | nativo ARM64 | `cores\windows-x64` (+ x86 opcional) | no **core host** x64/x86 (`OpenEmu.CoreHost.exe`) sob a emulação do Windows 11/10 ARM |

O buildbot libretro não publica cores nativos para Windows ARM64, então o app ARM64 usa um processo auxiliar
(como os helpers XPC do OpenEmu original): o core roda no `OpenEmu.CoreHost.exe` x64, o vídeo e o teclado passam
por memória compartilhada e os comandos por named pipe. Cores OpenGL não funcionam nesse modo; o app escolhe o
core por software equivalente. Detalhes e comandos em [docs/EMPACOTAMENTO-WINDOWS.md](docs/EMPACOTAMENTO-WINDOWS.md).

## OpenGL (N64, PlayStation HW, PSP, Dreamcast, DS, GameCube, PS2)

No Windows o Avalonia desenha via ANGLE (OpenGL ES sobre Direct3D), que não serve para cores que exigem OpenGL de
desktop 3.3+. Por isso a janela de jogo cria um **HWND filho nativo com contexto WGL próprio** (`Win32GlContext`):
o core roda e apresenta na thread de emulação, com vsync opcional, e a UI continua em ANGLE. A CI valida esse
caminho rodando o mupen64plus-next com Mesa/llvmpipe e exigindo frames reais.

## BIOS

| Situação | O que o OpenEmu faz |
|---|---|
| PlayStation | Instala automaticamente o **OpenBIOS** (PCSX-Redux, MIT) como `scph5500/5501/5502.bin`; Beetle PSX, SwanStation e PCSX-ReARMed iniciam com ele. Uma BIOS original, se você a extrair do seu console, pode substituir os arquivos na pasta `BIOS`. |
| PS2, Dreamcast, Saturn, DS, GBA, Pokémon mini, Vectrex, Jaguar… | Os cores padrão têm BIOS HLE/embutida (Play!, Flycast, Yabause, melonDS DS, mGBA, PokeMini, vecx, VirtualJaguar); a falta da BIOS original não bloqueia o jogo. |
| MSX, PSP, GameCube | "Instalar arquivos de sistema livres" baixa C-BIOS (blueMSX), assets do PPSSPP e Sys do Dolphin. |
| 3DO, PC Engine CD, Sega CD, Famicom Disk System, Lynx, ColecoVision, Odyssey², Intellivision, Atari 8-bit, Neo Geo (arcade) | Precisam da BIOS original: são imagens protegidas por copyright e **não são baixadas pelo app**. A tela Preferências → BIOS lista o nome exato de cada arquivo para você importar o dump do seu hardware. |

## Sistemas e cores

Os 42 sistemas do OpenEmu com os cores correspondentes (primeiro = padrão):

3DO (opera) · Arcade (fbneo, mame2003_plus) · Atari 2600 (stella) · 5200 (a5200) · 7800 (prosystem) · Atari 8-bit (atari800) ·
ColecoVision (gearcoleco, bluemsx) · Commodore 64 (vice_x64sc) · Dreamcast (flycast) · Game Boy / Color (gambatte, sameboy, mgba) ·
Game Boy Advance (mgba) · GameCube (dolphin) · Game Gear, Master System, SG-1000, Mega Drive, Mega-CD (genesis_plus_gx, picodrive) ·
32X (picodrive) · Intellivision (freeintv) · Jaguar (virtualjaguar) · Lynx (handy) · MSX (bluemsx) · Nintendo 64 (mupen64plus_next) ·
Nintendo DS (melondsds, desmume) · NES / Famicom Disk System (fceumm, nestopia, mesen) · Neo Geo Pocket (mednafen_ngp) · Odyssey² (o2em) ·
TurboGrafx-16 / CD / SuperGrafx (mednafen_pce) · PC-FX (mednafen_pcfx) · PlayStation (mednafen_psx_hw, swanstation, pcsx_rearmed) ·
PlayStation 2 (pcsx2, play) · PSP (ppsspp) · Pokémon mini (pokemini) · Saturn (mednafen_saturn, kronos) · Supervision (potator) ·
VMU (vemulator) · Vectrex (vecx) · Virtual Boy (mednafen_vb) · WonderSwan (mednafen_wswan)

O catálogo completo (extensões, controles, mapa de teclado, mapa RetroPad, cores) está em
`src/OpenEmu.Core/Resources/systems.json`; os cores (firmwares, extensões, URLs) em `cores.json`.
Ambos são gerados a partir dos plugins do OpenEmu e do `libretro-core-info`.

## Build

Requisitos: .NET SDK 9.

```powershell
cd Windows
dotnet build OpenEmu.Windows.sln
dotnet test                      # 25 testes (inclui carga real de um core libretro, em processo e via core host)
dotnet run --project src/OpenEmu.App
```

Pacote de distribuição com todos os cores (`dist\OpenEmu-Windows-<arch>.zip`):

```powershell
.\scripts\build-windows.ps1 -Arch x64      # x86 | arm64  (ou scripts/build-windows.sh <arch> no macOS/Linux)
```

Guia completo: [docs/EMPACOTAMENTO-WINDOWS.md](docs/EMPACOTAMENTO-WINDOWS.md).
CI: `.github/workflows/windows.yml` compila, testa (inclusive 32-bit e via core host) e publica os três zips como artefatos.

### CLI

```
openemu-cli systems | cores | install --all | bios | import <arquivos> | library
openemu-cli run --core fceumm --rom jogo.nes --frames 300 --screenshot out.png --state
```

## Pastas

| Windows | macOS (OpenEmu) |
|---|---|
| `%APPDATA%\OpenEmu\settings.json`, `Cores\`, `openvgdb.sqlite` | `~/Library/Application Support/OpenEmu` |
| `Documentos\OpenEmu Library\` → `roms\`, `Battery Saves\`, `Save States\`, `Screenshots\`, `Artwork\`, `BIOS\`, `Library.sqlite` | `Game Library/` |

Variáveis: `OPENEMU_HOME`, `OPENEMU_LIBRARY`, `OPENEMU_CORES`.

## Arquitetura

```
src/OpenEmu.Core   biblioteca .NET (sem UI): Libretro (loader P/Invoke + ambiente), Emulation (thread, pacing, SRAM,
                   states, cheats), Video (conversão de pixels, PNG), Audio (WASAPI), Input (RetroPad, teclado HID,
                   XInput), Systems/Cores/Bios (catálogos), Library (SQLite, importer, OpenVGDB), Homebrew, Localization
src/OpenEmu.App    Avalonia UI 11: MainWindow (biblioteca), GameWindow (+ GameView software / GlGameView OpenGL),
                   PreferencesWindow (Biblioteca, Jogo, Controles, Cores, BIOS), janelas de states/cheats/opções
src/OpenEmu.Cli    front-end headless (testes, scripts, CI)
src/OpenEmu.CoreHost processo auxiliar que roda um core fora do app (Remote/: memória compartilhada + named pipe)
tests/             xUnit
```

O código do OpenEmu original (Swift/Objective-C, Cocoa) continua na raiz do repositório e serve de referência;
os plugins de sistema (`OpenEmu/SystemPlugins/*/`) são a fonte do `systems.json`.

## Limitações conhecidas (v0.1)

- Cores OpenGL: validados na CI com Mesa/llvmpipe (mupen64plus-next); cores Vulkan/D3D não são suportados. No ARM64 (core host) continuam indisponíveis.
- Shaders (OpenEmu-Shaders/slang) não portados — apenas nearest/linear.
- Gamepads: XInput (Xbox e compatíveis); DirectInput/HID genérico não implementado.
- Arquivos 7z não são extraídos (ZIP sim); conjuntos de arcade são usados zipados.
- Em x64/x86 cada jogo roda no processo do app por padrão (ative "Executar cores em um processo separado" para isolamento); um jogo por vez é o cenário testado.
- ARM64: sem cores nativos (limitação do buildbot libretro); cores OpenGL indisponíveis via core host; Windows 10 ARM exige o pacote com cores x86.

---

### English

OpenEmu for Windows is a .NET 9 / Avalonia port of the OpenEmu library + emulator front-end for Windows x64, x86 and
ARM64. Emulation comes from libretro cores mapped to all 42 OpenEmu systems; each distribution bundles every core
available for its architecture (ARM64 runs the x64/x86 cores in a separate core-host process under Windows' emulation). See the tables above for the
feature list, `scripts/build-windows.ps1` for packaging and `openemu-cli` for headless use.
