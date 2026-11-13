# 歌词设置与动画参考

本轮参考了 [CeruMusic](https://github.com/timeshiftsauce/CeruMusic) 的设置组织和歌词动态行为，检查版本为 `9428b4aa1d63f5492a6bad2a50fba1dfa0b5041c`。

## 许可证

仓库 [LICENSE](https://github.com/timeshiftsauce/CeruMusic/blob/9428b4aa1d63f5492a6bad2a50fba1dfa0b5041c/LICENSE) 明确写明 `SPDX-License-Identifier: AGPL-3.0-only`，版权为 `Copyright (C) 2026 时迁酱`。GitHub API 将这个简短、链接到完整许可文本的文件标为 `NOASSERTION`，不代表没有许可证。仓库 README 另有非商业用途说明，并说明其 AMLL 歌词组件也采用 AGPL。

Embistic 的新设置窗口使用独立编写的 C# / WPF 控件、布局、预览及动画。没有复制 CeruMusic 或 AMLL 的实现代码、CSS、字体、图片或其他资产，也没有引入它们的运行时依赖。参考仓库仅放在本地 `artifacts/reference` 供检查，不属于应用发布内容。

## 检查到的行为

- [LyricFontSettings.vue](https://github.com/timeshiftsauce/CeruMusic/blob/9428b4aa1d63f5492a6bad2a50fba1dfa0b5041c/src/renderer/src/components/Settings/LyricFontSettings.vue)：系统字体列表、多个字体按顺序回退、歌词字号倍率、字重，以及即时文字预览。
- [DesktopLyricStyle.vue](https://github.com/timeshiftsauce/CeruMusic/blob/9428b4aa1d63f5492a6bad2a50fba1dfa0b5041c/src/renderer/src/components/Settings/DesktopLyricStyle.vue)：桌面字号和字重、主色、阴影色、对齐、文字背景遮罩、动画开关、翻译、双行和歌曲信息；支持应用及还原。
- [DeskTopLyric.vue](https://github.com/timeshiftsauce/CeruMusic/blob/9428b4aa1d63f5492a6bad2a50fba1dfa0b5041c/src/renderer/src/views/DeskTopLyric/DeskTopLyric.vue)：换句时的滑动与透明度过渡、行位置和字号变化、下一行缩小显示，以及关闭动画时立即更新。

## Embistic 的对应实现

原生“歌词外观”窗口分为字体、颜色与阴影、布局、动画四页，右侧持续显示主播放器和桌面悬浮窗预览。字体支持从本机列表选择或直接输入逗号分隔的回退列表；颜色支持 `#RRGGBB` 和 `#AARRGGBB`，输入未完成时保留上次有效预览，并在窗口底部提示，不弹出对话框。

窗口提供原生滑动、淡入、立即切换及动画时长、滚动时长、当前句聚焦倍率，支持单次和循环预览。预览示例文字可直接编辑。字体、颜色、阴影、背景透明度和布局修改会通过 `PreviewChanged` 实时传递给主窗口；保存时返回独立设置副本，取消时由主窗口恢复打开前的副本。关闭窗口后停止预览定时器，并丢弃待执行的预览更新。

没有把逐字高亮宣称为当前 LRC 的时间精度：普通逐行 LRC 仍按其实际行时间同步；翻译是否可用取决于取得的歌词内容。
