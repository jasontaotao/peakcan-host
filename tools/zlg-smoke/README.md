# zlg-smoke — ZLG USBCANFD 只读诊断工具

用于现场排查“ZLG 设备连不上 / 通道列表为空”。工具只做枚举、初始化、监听，
**不发送任何报文**。

## 运行前准备

1. 已安装 ZLG USBCANFD 驱动（设备管理器里能看到设备）。
2. 设备已插好，总线已接好（监听需要有其他节点发帧才能看到数据）。
3. 关闭 PeakCan.Host 以及其他占用该 CAN 盒的程序。
4. 必须保留完整发布目录：`zlg-smoke.exe`、`zlgcan.dll`、`kerneldlls\` 和
   `zlgcan License.txt`。`ZCAN_OpenDevice` 会按 `kerneldlls\dll_cfg.ini` 加载型号插件，
   只复制 exe 或 `zlgcan.dll` 会导致设备打开失败。

## 运行

打开 `cmd`，进入该目录。

只枚举设备：

```bat
zlg-smoke.exe > smoke.txt 2>&1
```

经典 CAN 500k 监听 5 秒：

```bat
zlg-smoke.exe --type 41 --dev 0 --idx 0 --baud 500000 --listen 5s > smoke2.txt 2>&1
```

CAN FD（仲裁 1M / 数据 2M）监听 5 秒：

```bat
zlg-smoke.exe --type 41 --dev 0 --idx 0 --fd --listen 5s > smoke3.txt 2>&1
```

把生成的 `smoke*.txt` 全文回传。

## 参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `--type` | 41 | 41=USBCANFD-200U, 42=100U, 43=MINI, 59=800U, 76=400U |
| `--dev` | 0 | 设备索引 |
| `--idx` | 0 | 通道索引 |
| `--baud` | 500000 | 仲裁段波特率 |
| `--dbit` | 2000000 | FD 数据段波特率（仅 `--fd`） |
| `--fd` | off | 以 CAN FD 模式初始化 |
| `--listen` | — | 初始化并只读监听，如 `5s` / `500ms` |
| `--set` | — | 透传 `ZCAN_SetValue`，如 `--set 0/canfd_standard=1`（可重复） |

## 退出码

| 码 | 含义 |
|---|---|
| 0 | 发现设备 / 监听流程成功 |
| 1 | 参数错误 |
| 2 | `zlgcan.dll` 加载失败（缺失、位数不匹配、缺少入口） |
| 3 | 未发现设备（检查驱动、USB、设备型号） |
| 4 | `SetValue` / `InitCAN` / `StartCAN` 失败（输出里有每步返回码） |

## 构建 / 发布

```bash
dotnet publish tools/zlg-smoke -c Release -r win-x86 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o artifacts/zlg-smoke/
```

发布产物是目录形式。分发时必须打包整个目录，保留 `zlgcan.dll` 与
`kerneldlls\` 的相对位置；不要只分发 `zlg-smoke.exe`。

## 运行库来源与配对

当前匹配运行库来自 `BmsDUC-v2.5`：其 `zlgcan.dll` 与 Host 原有文件完全一致，
因此只补入配套插件，没有混用新版 DLL。关键文件指纹：

- `zlgcan.dll`（2024-06-06）SHA256：`BA3CF47DC56443F55EA4968C991F9E733273C89D5B4CBDCB8E7097D38BBCAD60`
- `kerneldlls/USBCANFD.dll`（2024-12-19）SHA256：`661CB18B7C588A8F8B6DF88D752802392C8C0B18766B6707235A67DAB7F44A9A`

`zlgcan.dll` 与 `kerneldlls` 必须作为一套运行库同步更新；不要从不同版本包中
单独替换其中一个文件。官方许可必须随二进制一起分发。

## 输出示例（正常枚举到 200U）

```text
== Environment ==
Is64BitProcess=False IntPtr.Size=4
DLL=...\zlgcan.dll exists=True
== ABI ==
can_frame=16 canfd_frame=72 receive=24 receivefd=80 initcfg=32 devinfo=80
...
type=41 idx=0 OpenDevice handle=0x...
  GetDeviceInf ret=1 can_Num=2 hwType=USBCANFD-200U serial=...
  CloseDevice ret=1
```
