"""用 DeepSeek 官方 logo（api-docs.deepseek.com/img/logo.svg）生成：
   app.ico      —— exe 图标，多尺寸，官方蓝 #4D6BFE、透明底
   tray16.b64 / tray32.b64 —— 托盘图标的 alpha 掩码（运行时按状态着色）
"""
import base64
import io
import os
import re
import subprocess
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC_SVG = os.path.join(HERE, "ds_logo.svg")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
BLUE = (0x4D, 0x6B, 0xFE)

# 1) SVG -> 1024px 透明底 PNG（用本机 Chrome 渲染，避免额外依赖）
svg = open(SRC_SVG, encoding="utf-8").read()
svg = svg.replace('width="50.000000" height="50.000000"', 'width="1024" height="1024"')
svg = svg.replace('fill="#000"', f'fill="rgb({BLUE[0]},{BLUE[1]},{BLUE[2]})"')
svg = re.sub(r'\sxmlns:xlink="[^"]*"', "", svg)

html = f"""<!doctype html><html><head><meta charset="utf-8"><style>
html,body{{margin:0;padding:0;width:1024px;height:1024px;background:transparent}}
svg{{display:block;width:1024px;height:1024px}}
</style></head><body>{svg}</body></html>"""
html_path = os.path.join(HERE, "render_logo.html")
open(html_path, "w", encoding="utf-8").write(html)

shot = os.path.join(HERE, "logo_1024.png")
if os.path.exists(shot):
    os.remove(shot)
subprocess.run([
    CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
    "--force-device-scale-factor=1",
    "--default-background-color=00000000",
    "--window-size=1024,1024",
    f"--screenshot={shot}", "file:///" + html_path.replace("\\", "/")
], check=True, timeout=120, capture_output=True)

raw = Image.open(shot).convert("RGBA")
print("渲染尺寸:", raw.size)

# 2) 裁到图案边界，再按统一比例居中放到方形画布（留 6% 边距，符合应用图标习惯）
alpha = raw.getchannel("A")
bbox = alpha.point(lambda v: 255 if v > 8 else 0).getbbox()
print("图案边界:", bbox)
glyph = raw.crop(bbox)
side = max(glyph.size)
canvas_side = int(round(side / (1 - 0.12)))
canvas = Image.new("RGBA", (canvas_side, canvas_side), (0, 0, 0, 0))
canvas.paste(glyph, ((canvas_side - glyph.width) // 2, (canvas_side - glyph.height) // 2), glyph)
canvas = canvas.resize((1024, 1024), Image.LANCZOS)
canvas.save(os.path.join(HERE, "logo_square.png"))

# 3) app.ico：各尺寸单独缩放，小尺寸轻微锐化
def render(size):
    img = canvas.resize((size, size), Image.LANCZOS)
    if size <= 48:
        from PIL import ImageFilter
        img = img.filter(ImageFilter.UnsharpMask(radius=0.6, percent=60, threshold=2))
    return img

sizes = [256, 128, 64, 48, 32, 24, 16]
imgs = [render(s) for s in sizes]
ico = os.path.join(HERE, "app.ico")
imgs[0].save(ico, format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[1:])
print("app.ico:", os.path.getsize(ico), "bytes")

# 4) 托盘掩码：只留 alpha（运行时整体着蓝色或橙色），并先做去锯齿
def mask(size):
    img = canvas.resize((size, size), Image.LANCZOS)
    px = img.load()
    for y in range(size):
        for x in range(size):
            r, g, b, a = px[x, y]
            # 半透明边缘按 alpha 保留形状，颜色统一为白（后面整体着色）
            px[x, y] = (255, 255, 255, a)
    return img

for size in (16, 32):
    img = mask(size)
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    b64 = base64.b64encode(buf.getvalue()).decode()
    path = os.path.join(HERE, f"tray{size}.b64")
    open(path, "w").write(b64)
    print(f"tray{size}.b64: {len(b64)} chars ({size}x{size})")

# 5) 直接生成 C# 常量文件，供托盘图标按状态着色
m16 = open(os.path.join(HERE, "tray16.b64")).read()
m32 = open(os.path.join(HERE, "tray32.b64")).read()
cs = f"""namespace DeepSeekPeakStatus;

/// <summary>
/// DeepSeek 鲸鱼图标的 alpha 掩码（16/32 px，由 make_logo.py 从官方 logo.svg 生成）。
/// 托盘按当前档位整体着色：空闲用官方蓝，峰时用警示橙。
/// </summary>
internal static class TrayMask
{{
    public const string M16 = "{m16}";

    public const string M32 = "{m32}";
}}
"""
open(os.path.join(HERE, "TrayMask.cs"), "w", encoding="utf-8").write(cs)
print("TrayMask.cs 已生成")

# 预览：把托盘效果画出来给用户看
strip = Image.new("RGBA", (16 * 8 + 32 * 4 + 60, 64), (255, 255, 255, 255))
x = 10
for size, color in [(16, BLUE), (16, (255, 140, 0)), (32, BLUE), (32, (255, 140, 0))]:
    m = mask(size)
    solid = Image.new("RGBA", (size, size), color + (255,))
    solid.putalpha(m.getchannel("A"))
    strip.paste(solid, (x, 16), solid)
    x += size + 12
strip.save(os.path.join(HERE, "tray_preview.png"))
print("预览:", os.path.join(HERE, "tray_preview.png"))
