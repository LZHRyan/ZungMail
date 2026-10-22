# ZungMail Logo 设计与使用说明

## 设计思路

放弃原图中多种元素堆叠的做法（信封、地球、弧线和圆点同时出现），改为**纯首字母 Z 徽标**。这样更简洁，辨识度也更高。

- **一笔折线构成 Z**：单路径、等宽 52px 笔画，斜笔与横笔自然衔接
- **锐利折角、圆润端点**：`linejoin: bevel` 让转角更干净利落，`linecap: round` 让端点显得精致，放大后不显廉价
- **蓝紫渐变底板**：`#6FB0FF → #2F63F7 → #2B18A6`，左上角叠加径向高光、底部叠加暗部，再添加 2.5px 内描边形成玻璃感；118/512 的圆角率接近 macOS、Windows 11 图标的比例
- **文字使用白色到淡蓝的渐变，并带柔和投影**：在 24px 小图标下仍能保持清晰轮廓
- **薄荷绿 `#22D3BF` 不进入图标**，保留为产品界面内的信号色（如未读标记），避免在小尺寸下形成干扰

## 色彩规范

| 名称 | 色值 | 用途 |
| --- | --- | --- |
| Brand Blue | `#2F63F7` | 主品牌色、图标渐变中段、字标强调色 |
| Deep Indigo | `#2B18A6` | 图标渐变末端 |
| Signal Mint | `#22D3BF` | 产品内信号色（未读、在线等），不进入图标 |
| Ink Navy | `#12235F` | 浅色底字标主色、正文文字 |

## 交付文件

目录：`ZungMail-Logo/`

| 文件 | 用途 |
| --- | --- |
| `zungmail-icon.svg` | 主图标（矢量源文件） |
| `zungmail-mark.svg` | 纯白单色标记，透明底，用于深色界面或水印 |
| `zungmail-lockup-light.svg` | 横版图标与字标组合，浅色底 |
| `zungmail-lockup-dark.svg` | 横版图标与字标组合，深色底 |
| `zungmail.ico` | Windows 图标，包含 16、24、32、48、64、128、256 七种尺寸 |
| `png/` | 9 档正方形 PNG（16 到 512）与两版组合标 @2x |
| `preview.html` | 规范预览页：亮色和暗色底板、尺寸适配、组合标、色板、设计说明 |

## 在 WPF 项目中使用

在项目根目录创建 `Assets` 文件夹，把 `zungmail.ico` 放进去，在 `.csproj` 中添加资源：

```xml
<ItemGroup>
  <Resource Include="Assets\zungmail.ico" />
</ItemGroup>
```

然后在 `MainWindow.xaml` 的 Window 标签上直接引用：

```xml
<Window x:Class="ZungMail.MainWindow"
        Icon="/Assets/zungmail.ico"
        Title="ZungMail">
```

打包成 EXE 时，也可以在项目属性里把 `ApplicationIcon` 指向该 ico 文件，这样任务栏、Alt+Tab 和文件资源管理器都会使用这个图标。

### 注意事项

- `zungmail.ico` 内部为 32bpp BMP 帧（非 PNG 压缩帧），可同时兼容 WPF 的 `Icon` 属性、`ApplicationIcon` 编译选项与旧版 `System.Drawing`。
- `System.Drawing.Icon` 请求 256px 时会退到 128px 返回，这是该类库的已知限制，不影响资源管理器与 WPF 显示；若要用它生成托盘图标，建议传 `new Icon(path, 128, 128)`。
- 小尺寸（16 / 24 px）下不要叠加描边或额外装饰，保持 Z 单色轮廓即可，避免糊成一团。
