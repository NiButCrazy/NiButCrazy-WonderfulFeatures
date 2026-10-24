"""生成「NiButCrazy 奇妙特性」插件图标。

用法：python assets/generate-icon.py
输出：assets/icon.png（512x512 RGBA）
依赖：Pillow
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 512
SCALE = 4
S = SIZE * SCALE


def lerp_color(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def star_points(cx, cy, outer, inner):
    points = []
    for index in range(8):
        radius = outer if index % 2 == 0 else inner
        angle = math.radians(-90 + index * 45)
        points.append((cx + radius * math.cos(angle), cy + radius * math.sin(angle)))
    return points


def main():
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 斜向蓝紫渐变
    gradient = Image.new("RGB", (S, S))
    draw = ImageDraw.Draw(gradient)
    top_left = (79, 124, 255)
    bottom_right = (168, 85, 247)
    for i in range(2 * S):
        t = i / (2 * S - 1)
        draw.line([(i, 0), (0, i)], fill=lerp_color(top_left, bottom_right, t))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, S - 1, S - 1], radius=int(S * 0.23), fill=255
    )
    canvas.paste(gradient, (0, 0), mask)

    # 像素风头像（8x8）
    hair = (59, 42, 26, 255)
    skin = (198, 136, 99, 255)
    skin_light = (214, 152, 112, 255)
    eye = (61, 59, 142, 255)
    nose = (160, 106, 74, 255)
    mouth = (122, 75, 53, 255)

    head = [
        [hair] * 8,
        [hair] * 8,
        [hair, skin, skin_light, skin_light, skin_light, skin_light, skin, hair],
        [hair, skin, skin_light, skin_light, skin_light, skin_light, skin, hair],
        [hair, skin, eye, skin_light, skin_light, eye, skin, hair],
        [hair, skin, skin_light, nose, nose, skin_light, skin, hair],
        [hair, skin, skin_light, mouth, mouth, skin_light, skin, hair],
        [hair, skin, skin, skin, skin, skin, skin, hair],
    ]

    origin_x, origin_y = 78, 160
    pixel = 27
    deco = ImageDraw.Draw(canvas)
    for row, colors in enumerate(head):
        for column, color in enumerate(colors):
            x0 = (origin_x + column * pixel) * SCALE
            y0 = (origin_y + row * pixel) * SCALE
            deco.rectangle(
                [x0, y0, x0 + pixel * SCALE - 1, y0 + pixel * SCALE - 1],
                fill=color,
            )

    # 半透明装饰统一画在单独图层，避免直接写入 alpha 像素
    overlay = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    overlay_draw = ImageDraw.Draw(overlay)

    # 背景装饰点
    for x, y, r, alpha in [
        (74, 80, 7, 60),
        (430, 70, 5, 50),
        (420, 420, 8, 45),
        (80, 430, 5, 40),
    ]:
        overlay_draw.ellipse(
            [x * SCALE - r * SCALE, y * SCALE - r * SCALE,
             x * SCALE + r * SCALE, y * SCALE + r * SCALE],
            fill=(255, 255, 255, alpha),
        )

    # 头像底部阴影
    overlay_draw.rounded_rectangle(
        [(origin_x - 8) * SCALE, (origin_y + pixel * 8 + 6) * SCALE,
         (origin_x + pixel * 8 + 8) * SCALE, (origin_y + pixel * 8 + 16) * SCALE],
        radius=6 * SCALE,
        fill=(20, 20, 40, 80),
    )

    # 星光光晕
    overlay_draw.ellipse(
        [285 * SCALE, 85 * SCALE, 425 * SCALE, 225 * SCALE],
        fill=(255, 240, 150, 45),
    )

    canvas = Image.alpha_composite(canvas, overlay)

    # 闪光星 + 小星 + 闪光点
    deco = ImageDraw.Draw(canvas)
    deco.polygon(
        [(x * SCALE, y * SCALE) for x, y in star_points(355, 155, 74, 22)],
        fill=(255, 255, 255, 255),
        outline=(255, 224, 102, 255),
        width=3 * SCALE,
    )
    deco.polygon(
        [(x * SCALE, y * SCALE) for x, y in star_points(420, 235, 30, 9)],
        fill=(255, 240, 170, 255),
    )
    deco.ellipse(
        [300 * SCALE, 240 * SCALE, 316 * SCALE, 256 * SCALE],
        fill=(255, 255, 255, 255),
    )
    deco.ellipse(
        [268 * SCALE, 100 * SCALE, 280 * SCALE, 112 * SCALE],
        fill=(255, 255, 255, 255),
    )

    canvas = canvas.resize((SIZE, SIZE), Image.LANCZOS)
    output = Path(__file__).resolve().parent / "icon.png"
    canvas.save(output, "PNG")
    print(f"已生成：{output}")


if __name__ == "__main__":
    main()
