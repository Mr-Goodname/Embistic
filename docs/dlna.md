# DLNA 支持范围

点击播放控制条的输出按钮，在弹出面板中搜索并选择 DLNA 音箱；「音频与投放」页面也保留完整设备管理入口。

Embistic 通过 SSDP 搜索当前所有启用的 IPv4 网络接口，识别提供 UPnP AVTransport 服务的播放设备。一次搜索最多等待 5 秒；设备需要已经开机，并与电脑处于可以互相访问的局域网。Windows 防火墙、VPN、访客 Wi-Fi 的客户端隔离及禁止组播的路由设置可能影响发现。

已实现的控制包括选择设备、投放曲目、播放/暂停/停止、跳转播放位置、读取进度和调整音量。是否支持每个控制由设备自身的 UPnP 服务决定；不支持的操作会显示明确错误。没有 RenderingControl 服务的设备需要使用实体音量键。设备列表只包含能直接接受音频播放命令的 DLNA 渲染器，不会把普通媒体服务器列为音箱。

播放时，设备直接从 Emby 服务器获取音频。电脑不会中转音频，也不需要开启新的 HTTP 服务或修改防火墙入站规则。播放地址带有当前 Emby 访问凭据，因此只应选择自己信任的局域网播放设备。程序不会在错误信息中显示完整音频地址或凭据。

当前投放保留曲目的原始格式。FLAC、ALAC、DSD 等格式能否播放取决于接收设备，不会自动把不支持的格式转码成 MP3。部分设备的解码、响应或进度更新较慢，桌面歌词同步精度受设备上报进度影响。设备之间没有同步多房间播放；暂不支持 AirPlay、Chromecast、蓝牙设备发现或在断网时保留投放队列。

部分音箱的 `ConnectionManager/GetProtocolInfo` 只声明 `http-get:*:*:*`，并不列出具体支持的音频编码。一次 FLAC 播放成功不能保证所有编码或采样率均兼容。

部分设备异步处理进度跳转，过快连续发送跳转和暂停可能让设备暂时停止更新进度。遇到这种情况可稍等片刻再操作。

协议检查位于 `tests/dlna`，包括模拟渲染器的 HTTP 控制流程。实体设备检查程序位于 `tests/dlna-live`，默认只读，只有明确传入 `--run` 才执行静音播放验证；使用前请阅读该目录的说明。测试结果不应包含访问凭据或带认证的音频 URL。

检查命令：

```powershell
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/dlna/DlnaChecks.csproj
powershell -ExecutionPolicy Bypass -File tools/build.ps1 run --project tests/dlna/DlnaChecks.csproj -- --discover
```
