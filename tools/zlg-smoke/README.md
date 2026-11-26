# zlg-smoke — ZLG USBCANFD 只读诊断工具

用于现场排查“ZLG 设备连不上 / 通道列表为空”。工具只做枚举、初始化、监听，
**不发送任何报文**。

## 运行前准备

1. 已安装 ZLG USBCANFD 驱动（设备管理器里能看到设备）。
2. 设备已插好，总线已接好（监听需要有其他节点发帧才能看到数据）。
3. 关闭 PeakCan.Host 以及其他占用该 CAN 盒的程序。
4. 只需要 `zlg-smoke.exe`（32 位单文件，已内嵌 `zlgcan.dll`）。
   如需替换 DLL，把 x86 的 `zlgcan.dll` 放到同目录，会优先使用同目录文件。

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
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o artifacts/zlg-smoke/
```

发布产物是单个 `zlg-smoke.exe`（`zlgcan.dll` 已内嵌，运行时自解压）。

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
