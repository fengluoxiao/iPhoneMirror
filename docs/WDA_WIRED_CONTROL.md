# 有线控制(WebDriverAgent over usbmux)设计与实现说明

> 状态:**已实现,已通过 Release 编译(0 警告 0 错误),待真机验收**。
> 本文档同时是设计说明与验收清单。

## 1. 背景与调研结论

现有蓝牙反向控制是 BLE HID over GATT(Windows 作外设,iOS 辅助触控消费,
`BluetoothHidMouseService.cs`)。发送端已做 8ms 节流与合并,但 Windows GATT Server
API 不暴露连接间隔等参数,延迟受系统蓝牙栈制约,改善空间很小。

调研结论(详见 2026-08 调研记录):

1. **"PC 虚拟 USB HID 设备喂给 iPhone"不可行**:桌面 xHCI 只有主机角色,
   Windows 没有 gadget 框架;且同一条线 iPhone 只能处于一种 USB 角色,
   有线投屏(iPhone=设备)与 USB 键鼠(iPhone=主机)物理上不能并存。
2. iOS 在设备模式下没有公开输入注入通道:usbmux 无 HID 服务、CarPlay 输入被
   MFi 认证锁死、iPhone Mirroring 输入走 AWDL 且绑定 Secure Enclave。
3. **唯一"软件即可、与有线投屏同线并存"的输入通道是 WebDriverAgent(WDA)**:
   WDA 以普通 App 形式运行在 iPhone 上,内部用 XCUITest 在系统层注入触摸/键盘,
   PC 经 usbmux 端口转发走 HTTP 驱动。usbmux 是多路复用协议,与 QuickTime
   采集互不干扰。社区已在 Windows 上验证多年(tidevice/iproxy/facebook-wda)。

WDA 能力:点击、长按、拖动/滑动、滚动、键盘文本、Home/锁定/音量等硬件按键,
支持多指(后续版本)。代价:手机需一次性安装签名的 WDA(免费 Apple ID 7 天
重签,付费账号一年),iOS 16+ 需开启开发者模式。

## 2. 方案设计

### 2.1 链路

```text
WPF 输入管线(已有)                iPhoneMirror.App(C#,本次新增)
PreviewPointerEventArgs ──► WdaControlService ──► 本地 TcpListener(127.0.0.1:动态)
(绝对源像素坐标)              │ (HttpClient HTTP)      │ 每个连接一条 usbmux 隧道
                              ▼                        ▼
                        WebDriverAgent(iOS App)  UsbmuxTunnelClient ──► Apple usbmuxd
                        设备端口 8100             (127.0.0.1:27015/37015,XML plist)
```

- usbmux 客户端按 `src/Core/src/Transport/UsbMuxClient.cpp` 的既有协议在 C# 复刻:
  16 字节 LE 头 `[长度, 版本=1, 类型=8, tag]` + XML plist 消息体;
  `ListDevices` 按 UDID 找 `DeviceID`;`Connect` 的 `PortNumber` 传 `htons(8100)`。
  不修改 C++ Core,不引入二进制 plist。
- 每个本地 TCP 连接独立建立一条设备隧道(与 iproxy 行为一致),
  HttpClient 的连接池会自然复用其中一条。
- WDA 接口:W3C `POST /session/{sid}/actions`(触摸注入,`origin:"viewport"`,
  坐标为逻辑点)、`/wda/keys`(文本)、`/wda/pressButton`(硬件按键)、
  `GET /status`(存活探测)、`POST /session`(会话)、`GET /window/size`(逻辑尺寸)。

### 2.2 交互设计

- 快捷操作栏新增 **有线控制** 按钮(与蓝牙控制互斥:启用一方会先停用另一方)。
- 启用流程:找到 usbmux 设备(找不到 → 提示需要 USB 连接)→ 启动端口转发 →
  轮询 `/status` → 建立会话 → 已连接。未连接期间始终弹出引导窗口,连接后自动
  变为已连接并在 5 秒后关闭;失败显示原因。
- 指针映射(绝对注入,与 BLE 相对指针不同):
  - 左键按下 → 记录起点;移动 → 按段下发拖动手势(最新优先,丢旧);松开 →
    若累计位移小于阈值则补一次点击,否则补最后一段;
  - 右键 → 长按(700ms);滚轮 → 在当前点上下翻页(轻扫);
  - 悬停移动不注入(iOS 无 hover)。
- 键盘:VK→字符(美式布局),逐键走 `/wda/keys`;Enter→`\n`、Backspace→`\b`。
  需要先点一下手机上的输入框让键盘弹出;中文 IME 暂不支持。
- 连接后显示 4 个手机按键:主屏 / 锁屏 / 音量+ / 音量−。
- 坐标换算:预览绝对坐标 → 源像素(`MapPointerToSource` 已有)→ 逻辑点
  (`window/size` / 源分辨率,按比例缩放,横竖屏自洽)。
- 会话健康:已连接状态每 4 秒探测 `/status`,失败退回等待态并自动重建会话;
  单请求遇 invalid session 自动重建重试一次。
- 目标设备会话结束/移除时自动停用(对齐蓝牙控制的
  `NotifyCaptureSessionChanged` 逻辑);主窗口关闭时停用。

### 2.3 文件清单

| 文件 | 动作 | 说明 |
|---|---|---|
| `src/App/Services/UsbmuxTunnelClient.cs` | 新增 | C# usbmuxd 客户端(ListDevices/Connect) |
| `src/App/Services/WdaPortForwarder.cs` | 新增 | 本地 TCP → 设备 8100 桥接 |
| `src/App/Services/WdaControlService.cs` | 新增 | WDA 会话/手势/文本/按键,状态机 |
| `src/App/Windows/WdaControlNoticeWindow.xaml(.cs)` | 新增 | 有线控制引导/已连接/失败弹窗 |
| `src/App/ViewModels/MainViewModel.cs` | 修改 | 状态、命令、生命周期、与 BLE 互斥 |
| `src/App/MainWindow.xaml(.cs)` | 修改 | 按钮、手机按键行、输入路由 |
| `src/App/Localization/Strings.*.xaml` | 修改 | 三语字符串 |
| `docs/USER_GUIDE.md` / `README.md` / `README.en.md` | 修改 | 功能说明与安装引导 |

### 2.4 明确不做(v1)

- 独立窗口的反向控制仍走蓝牙(有线控制仅作用于主预览选中设备);
- 多指手势(双指缩放)、双击、硬件组合键(Ctrl+C 等)、中文 IME;
- 自动拉起 WDA(iOS 17+ 需 CoreDevice 隧道,后续用 pymobiledevice3 思路补);
- WDA 的签名安装集成(仅提供引导文档)。

## 3. 验收清单(明早真机步骤)

1. 构建:`dotnet build src/App/iPhoneMirror.App.csproj -c Release`(应零错误)。
2. 准备:iPhone 数据线连接并信任;投屏中;手机已装 WDA(见 USER_GUIDE 新章节)
   且开启开发者模式;在手机上点开 WebDriverAgent 应用(白屏即正常)。
3. 点击快捷操作栏"启用有线控制" → 弹窗显示等待 → 数秒内变"已连接"并自动关闭。
4. 鼠标左键点预览 → 手机对应位置出现点击;拖动 → 滑动/滚动;右键 → 长按菜单;
   滚轮 → 列表滚动;主屏/锁屏/音量按钮生效;点手机搜索框后用键盘输入英文。
5. 断开 USB:状态自动退回等待/失败,重新插上并重开 WDA 后可恢复。
6. 回归:蓝牙控制功能不受影响(两者互斥启用)。

## 4. 风险与备注

- WDA 版本与 iOS 版本需匹配(社区预编译 ipa 一般标注适配范围);
- 免费签名 7 天过期,过期后 WDA 无法启动,需重签(引导文档已写明);
- `/wda/keys` 依赖焦点文本控件,无键盘焦点时输入无效(设计使然,非 bug);
- 拖动按段下发,连续性略逊真实手指;若后续不够顺滑,再评估 WDA
  websocket 流式方案。
