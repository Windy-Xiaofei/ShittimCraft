# ShittimCraft

**Minecraft 搬进蔚蓝档案（Blue Archive）的客户端里。**

项目名 **ShittimCraft**。插件内部标识沿用早期的 `mcworld`（配置文件名和插件数据目录仍然用它），编译产物是 `SchaleMcWorld.dll`。

一个 BepInEx 6（IL2CPP）插件，在游戏进程内重建一层 Minecraft 玩法：史蒂夫、方块世界、破坏与放置、快捷栏、背包、第一人称视角和斜俯视地牢相机。不是外挂，不改服务端数据，纯粹在客户端加一层可开关的世界。

**[中文](#中文) · [English](#english)**

---

## 中文

### 这是什么

蔚蓝档案是 Unity + IL2CPP 的游戏。这个插件通过 BepInEx 注入托管层，然后：

- 在战斗场景里生成一个 **史蒂夫**（1.8 格高的人形模型，手脚会摆）
- 盖一层 **方块世界**——9 种方块，可放置、可破坏，网格自己生成
- 把 **斜俯视相机**（类似 Minecraft Dungeons）接到主相机上，F8 切 **第一人称**
- 画一套 **Minecraft 风格 HUD**：快捷栏、准星、物品栏、聊天指令栏
- 让学生 **被方块挡住**（AABB 挤出），史蒂夫也能撞到学生

全部用 C# 写在 `src/`，约 8900 行。

### 环境要求

| 项 | 版本 |
|---|---|
| 蔚蓝档案 | Steam 版（Unity 2021.3.56f1） |
| BepInEx | 6.x，IL2CPP，x64（be.7xx 测试通过） |
| .NET SDK | 6.0 或更高（编译目标 `net6.0`） |

游戏目录必须先装好 BepInEx 并**至少启动过一次** —— `BepInEx\interop\` 里的 IL2CPP 程序集是运行时生成的，没有它编译不了。

### 构建

```powershell
cd src
dotnet build -c Release
```

插件会自动找到游戏目录（依次尝试 `-p:GameDir=`、环境变量 `BLUEARCHIVE_DIR`、几个常见 Steam 库路径）。装到别处就显式指定：

```powershell
dotnet build -c Release -p:GameDir="E:\Games\BlueArchive"
```

编译成功后 `SchaleMcWorld.dll` 会自动复制到 `BepInEx\plugins\`。

### 安装

```
<游戏目录>\BepInEx\plugins\
├── SchaleMcWorld.dll
└── McWorld\
    ├── mcassets          (可选，着色器包)
    └── textures\         (17 张 16×16 贴图)
```

贴图**不在仓库里**（版权属于 Mojang）。用脚本从你自己的 Minecraft 安装提取：

```powershell
python scripts/fetch-textures.py "C:\Users\<你>\AppData\Roaming\.minecraft\versions\1.21.4\1.21.4.jar"
```

或者手动把 `assets/minecraft/textures/block/` 下的同名 png 复制到 `textures\`。缺贴图插件也能跑，只是方块变成纯色。

### 操作

| 按键 | 作用 |
|---|---|
| **F7** | 进入 / 退出方块世界 |
| **F8** | 斜俯视 ↔ 第一人称 |
| **E** | 打开背包 |
| **W A S D** | 移动 |
| **空格** | 跳跃 |
| **Shift** | 下蹲 |
| **左键** | 挖方块 / 挥剑 |
| **右键** | 放置方块 |
| **1 – 9** | 选快捷栏 |
| **T** 或 **/** | 打开指令栏 |
| **Tab** | 第一人称下锁定 / 释放鼠标 |
| **Q** | 切换 互动 / 破坏 模式 |
| **鼠标滚轮** | 切换方块 |

第一人称下鼠标默认锁定视角，按 **Tab** 释放光标去点游戏本来的界面。

### 指令

游戏内按 `T` 输入，`~` 代表史蒂夫脚下那格：

```
/give <方块|pickaxe|sword|shield|pearl>
/inv                    打开背包
/setblock ~ ~1 ~ stone  放置方块
/fill <x1 y1 z1> <x2 y2 z2> <方块|air>   区域填充（上限 2048 格）
/tp <x y z> | /tp here
/pos                    当前坐标
/clear                  清空所有方块
/time set <day|night>
/speed <倍率>  /fly
/fp  /3d                第一人称 / 斜俯视
/mine                   切换 互动 / 破坏 模式
/hurt <0-1>             剑的伤害比例
/selftest               跑一遍自检
```

完整列表在 `/help`。

### 配置

首次运行后在 `BepInEx\config\com.shittim.ba.mcworld.cfg` 生成，常用的几项：

| 键 | 默认 | 说明 |
|---|---|---|
| `World.ToggleKey` | `F7` | 进出方块世界的按键 |
| `World.UiModeKey` | `F8` | 相机模式切换 |
| `World.SteveHeight` | `1.8` | 史蒂夫身高（格） |
| `Camera.CameraDistance` | `13` | 相机距离 |
| `Camera.CameraPitch` | `55` | 俯角 |
| `Camera.FirstPersonFov` | `74` | 第一人称视野 |
| `Combat.SwordDamage` | `1.0` | 每刀扣最大生命的比例 |
| `Misc.HideConsole` | `true` | 隐藏 BepInEx 控制台窗口 |

### 实现上值得说的几点

**1. 运行时生成的纹理活不下来。**
BA 会周期性清扫没有原生引用的资产。强 GCHandle、`HideFlags.DontSave`、隐藏场景引用全都没用——纹理照样被销毁，然后 IMGUI 每帧抛 `ObjectCollectedException`。最后的办法是**彻底不生成纹理**：所有 UI 面板改用引擎内置的 `whiteTexture` 分层画纯色矩形（阴影 / 主体 / 描边三层），零句柄、零重建。贴图改成从磁盘读 PNG 字节并带节流缓存。

**2. 相机接管要挑对象。**
直接禁用 `CameraConfigReceiver`、`RenderConfigLoader` 这些 URP 组件会让画面出现黑块。改成白名单：只接管真正的相机驱动脚本，`CameraConfig` / `Light` / `Shadow` / `Audio` 一律放过，退出时原样还原 fov、near、far、正交标志。

**3. 输入要绕开 Unity。**
游戏的输入层会把鼠标吃掉。插件用 `SetWindowLongPtr` 子类化窗口过程装了一个原生钩子，直接收 `WM_CHAR` / `WM_KEYDOWN`，这样指令栏能输入中文以外的字符，也不和游戏自己的按键冲突。

**4. 方块网格复用一套贴图。**
9 种方块共用一个 `Mesh`，靠材质数组切换贴图，避免每种方块一个 draw call。

**5. 自检。**
启动时跑一遍资产 / UI / 运行时检查，结果写到 `BepInEx\plugins\McWorld\selftest.log`，排查环境问题时先看这个文件。

### 已知限制

- 只在战斗场景和咖啡厅里稳定，主菜单下没有学生当锚点，方块世界是空的
- 学生被方块挡住是**视觉挤出**（每帧按 AABB 推位置），不改寻路——绕过墙角后学生仍会走向原目标
- 史蒂夫不会掉血、不能死，没有生存要素
- 只测过 Steam 版 2021.3.56f1，其它版本 interop 可能对不上

### 免责声明

个人学习项目，与 Nexon / NEXON Games / Mojang 均无关联。不分发任何游戏资源，贴图需自行从自己的 Minecraft 安装中提取。使用风险自负。

MIT License。

---

## English

### What this is

**ShittimCraft** is the project. The plugin keeps its earlier internal id `mcworld` — that's the name in the config file and the plugin data folder — and it builds to `SchaleMcWorld.dll`. Blue Archive runs on Unity + IL2CPP; the plugin injects a managed layer through BepInEx and then:

- spawns a **Steve** in the battle scene — 1.8 blocks tall, arms and legs swinging
- builds a **block world** on top of it: nine block types, place and break, mesh generated at runtime
- takes over the main camera with a **dungeon-style top-down view**, `F8` flips to **first person**
- draws a **Minecraft HUD**: hotbar, crosshair, inventory, chat command bar
- makes students **collide with blocks** (AABB push-out), and Steve bumps into them too

All of it is plain C# under `src/`, roughly 8,900 lines.

### Requirements

| Item | Version |
|---|---|
| Blue Archive | Steam release (Unity 2021.3.56f1) |
| BepInEx | 6.x, IL2CPP, x64 (tested on be.7xx) |
| .NET SDK | 6.0+ (targets `net6.0`) |

BepInEx has to be installed in the game folder **and the game launched at least once** — the IL2CPP assemblies under `BepInEx\interop\` are generated at runtime, and the build fails without them.

### Build

```powershell
cd src
dotnet build -c Release
```

The project locates the game automatically (`-p:GameDir=`, then `BLUEARCHIVE_DIR`, then a few common Steam library paths). Point it somewhere else explicitly:

```powershell
dotnet build -c Release -p:GameDir="E:\Games\BlueArchive"
```

On success `SchaleMcWorld.dll` is copied into `BepInEx\plugins\`.

### Install

```
<game folder>\BepInEx\plugins\
├── SchaleMcWorld.dll
└── McWorld\
    ├── mcassets          (optional shader bundle)
    └── textures\         (17 × 16×16 textures)
```

Textures are **not in the repo** (they belong to Mojang). Pull them out of your own Minecraft install:

```powershell
python scripts/fetch-textures.py "C:\Users\<you>\AppData\Roaming\.minecraft\versions\1.21.4\1.21.4.jar"
```

The plugin still runs without them — blocks just come out flat-coloured.

### Controls

| Key | Action |
|---|---|
| **F7** | enter / leave the block world |
| **F8** | top-down ↔ first person |
| **E** | inventory |
| **W A S D** | move |
| **Space** | jump |
| **Shift** | sneak |
| **Left click** | mine / swing |
| **Right click** | place |
| **1 – 9** | hotbar slot |
| **T** or **/** | command bar |
| **Tab** | grab / release the mouse in first person |
| **Q** | toggle interact / destroy |
| **Wheel** | cycle blocks |

### Commands

Type `T` in game. `~` means the block Steve stands on.

```
/give <block|pickaxe|sword|shield|pearl>
/inv
/setblock ~ ~1 ~ stone
/fill <x1 y1 z1> <x2 y2 z2> <block|air>   (up to 2048 blocks)
/tp <x y z> | /tp here
/pos
/clear
/time set <day|night>
/speed <mul>  /fly
/fp  /3d
/mine                   toggle interact / destroy
/hurt <0-1>             sword damage fraction
/selftest
```

`/help` lists everything.

### Config

Written to `BepInEx\config\com.shittim.ba.mcworld.cfg` on first run:

| Key | Default | Meaning |
|---|---|---|
| `World.ToggleKey` | `F7` | enter/leave the world |
| `World.UiModeKey` | `F8` | camera mode |
| `World.SteveHeight` | `1.8` | Steve's height in blocks |
| `Camera.CameraDistance` | `13` | camera distance |
| `Camera.CameraPitch` | `55` | downward angle |
| `Camera.FirstPersonFov` | `74` | first-person FOV |
| `Combat.SwordDamage` | `1.0` | max-HP fraction per hit |
| `Misc.HideConsole` | `true` | hide the BepInEx console |

### Notes on the implementation

**Runtime-created textures do not survive.** Blue Archive periodically sweeps assets that have no native reference. A strong GCHandle, `HideFlags.DontSave`, and keeping the object in a hidden scene all fail — the texture is destroyed anyway, and IMGUI then throws `ObjectCollectedException` every frame. The fix was to stop creating textures entirely: every HUD panel is drawn as layered solid rectangles (shadow / body / outline) on the engine's built-in `whiteTexture`. Zero handles, zero rebuilds. Block textures are read from disk as PNG bytes with a throttled cache instead.

**Camera takeover needs a whitelist.** Disabling URP components like `CameraConfigReceiver` or `RenderConfigLoader` produces black patches on screen. The plugin only touches actual camera driver scripts, leaves `CameraConfig` / `Light` / `Shadow` / `Audio` alone, and restores fov, near, far and the orthographic flag on exit.

**Input has to bypass Unity.** The game's input layer swallows the mouse. A native window-procedure hook (`SetWindowLongPtr`) captures `WM_CHAR` / `WM_KEYDOWN` directly, so the command bar can be typed into without fighting the game's own bindings.

**One mesh, many textures.** All nine block types share a single mesh and switch textures through material arrays.

**Self test.** An asset/UI/runtime check runs at startup and writes to `BepInEx\plugins\McWorld\selftest.log`. That file is the first thing to look at when something breaks.

### Known limitations

- Stable in battle scenes and the cafe; the main menu has no students to anchor to, so the world is empty there
- Student collision is **visual push-out** per frame, not pathfinding — they still walk toward their original target after clearing a corner
- Steve has no health and cannot die; there is no survival layer
- Only tested against the Steam build 2021.3.56f1

### Disclaimer

A personal learning project. Not affiliated with Nexon, NEXON Games, or Mojang. No game assets are distributed here — textures must be extracted from your own Minecraft installation. Use at your own risk.

MIT License.
