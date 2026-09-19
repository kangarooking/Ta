"""从品牌 PNG 生成 Windows 用的 app.ico（windows/assets/app.ico）。

exe 图标（MSBuild 的 <ApplicationIcon>）必须是 .ico，而仓库里只有品牌 PNG ——
这个脚本负责转换，于是仓库不必提交二进制产物，换图标时重新跑一次即可。

用法：
    python windows/tools/make_appicon.py

依赖：Pillow（pip install pillow）
"""
import os
import sys

from PIL import Image

# Windows 会按显示场景挑合适的一档，七档是最常见的组合（16/24/32/48/64/128/256）。
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCE = os.path.join(ROOT, "Resources", "Brand", "Ta-AppIcon.png")
TARGET = os.path.join(ROOT, "windows", "assets", "app.ico")


def main() -> None:
    if not os.path.exists(SOURCE):
        sys.exit(f"找不到品牌图标：{SOURCE}")

    os.makedirs(os.path.dirname(TARGET), exist_ok=True)

    # 用 RGBA 保留圆角透明区域 —— ico 的 alpha 通道由 Pillow 直接写进去。
    Image.open(SOURCE).convert("RGBA").save(TARGET, format="ICO", sizes=SIZES)

    print(f"已生成 {TARGET}（{os.path.getsize(TARGET)} 字节，{len(SIZES)} 档尺寸）")


if __name__ == "__main__":
    main()
