from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


REPO_ROOT = Path(__file__).resolve().parents[3]
CANVAS_SIZE = 1024
FACE_GRID = (7, 25, 54, 92)
TILE = {
    "coral": "#D56673",
    "peach": "#DA884F",
    "yellow": "#C8BC3F",
    "mint": "#4CAA62",
    "sky": "#3198C8",
    "lavender": "#8066C4",
}


def lerp(start: tuple[int, int], end: tuple[int, int], amount: float) -> tuple[int, int]:
    return (round(start[0] + (end[0] - start[0]) * amount), round(start[1] + (end[1] - start[1]) * amount))


def face_point(points: list[tuple[int, int]], horizontal: float, vertical: float) -> tuple[int, int]:
    top_left, top_right, bottom_right, bottom_left = points
    top = lerp(top_left, top_right, horizontal)
    bottom = lerp(bottom_left, bottom_right, horizontal)
    return lerp(top, bottom, vertical)


def draw_face(image: Image.Image, points: list[tuple[int, int]], colors: list[str]) -> None:
    draw = ImageDraw.Draw(image, "RGBA")
    for row in range(2):
        for column in range(2):
            left = column / 2
            right = (column + 1) / 2
            top = row / 2
            bottom = (row + 1) / 2
            cell = [
                face_point(points, left, top),
                face_point(points, right, top),
                face_point(points, right, bottom),
                face_point(points, left, bottom),
            ]
            draw.polygon(cell, fill=colors[row * 2 + column], outline=FACE_GRID, width=5)
    draw.line(points + [points[0]], fill=FACE_GRID, width=5, joint="curve")


def affine_letter(label: str, points: list[tuple[int, int]], color: str) -> Image.Image:
    source_size = 420
    source = Image.new("RGBA", (source_size, source_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(source)
    font_path = Path("C:/Windows/Fonts/arialbd.ttf")
    font = ImageFont.truetype(str(font_path), round(source_size * 3 / 4))
    bounds = draw.textbbox((0, 0), label, font=font)
    text_width = bounds[2] - bounds[0]
    text_height = bounds[3] - bounds[1]
    draw.text(
        ((source_size - text_width) / 2 - bounds[0], (source_size - text_height) / 2 - bounds[1]),
        label,
        font=font,
        fill=color,
        stroke_width=12,
        stroke_fill=(7, 25, 54, 220),
    )

    top_left, top_right, _, bottom_left = points
    x_axis = (top_right[0] - top_left[0], top_right[1] - top_left[1])
    y_axis = (bottom_left[0] - top_left[0], bottom_left[1] - top_left[1])
    determinant = x_axis[0] * y_axis[1] - x_axis[1] * y_axis[0]
    inverse = (
        y_axis[1] / determinant,
        -y_axis[0] / determinant,
        -x_axis[1] / determinant,
        x_axis[0] / determinant,
    )
    scale = source_size
    coefficients = (
        scale * inverse[0],
        scale * inverse[1],
        -scale * (inverse[0] * top_left[0] + inverse[1] * top_left[1]),
        scale * inverse[2],
        scale * inverse[3],
        -scale * (inverse[2] * top_left[0] + inverse[3] * top_left[1]),
    )
    return source.transform((CANVAS_SIZE, CANVAS_SIZE), Image.Transform.AFFINE, coefficients, Image.Resampling.BICUBIC)


def build_icon() -> Image.Image:
    image = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    top = [(512, 42), (946, 292), (512, 542), (78, 292)]
    left = [(78, 292), (512, 542), (512, 1014), (78, 764)]
    right = [(512, 542), (946, 292), (946, 764), (512, 1014)]
    faces = [
        (top, [TILE["yellow"], TILE["sky"], TILE["lavender"], TILE["coral"]], "P", "#FFFFFF"),
        (left, [TILE["peach"], TILE["mint"], TILE["sky"], TILE["yellow"]], "L", "#FFFFFF"),
        (right, [TILE["lavender"], TILE["peach"], TILE["coral"], TILE["mint"]], "M", "#FFFFFF"),
    ]
    for points, colors, label, text_color in faces:
        draw_face(image, points, colors)
        image.alpha_composite(affine_letter(label, points, text_color))
    return image


def main() -> None:
    icon = build_icon()
    ui_assets = REPO_ROOT / "src" / "pdm-ui" / "src" / "assets"
    desktop_assets = REPO_ROOT / "src" / "Pdm.Desktop" / "Assets"
    icon.save(ui_assets / "pdm-client-icon.png", optimize=True)
    icon.resize((64, 64), Image.Resampling.LANCZOS).save(ui_assets / "plm-favicon.png", optimize=True)
    ico_image = icon.resize((256, 256), Image.Resampling.LANCZOS)
    ico_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    for name in ("PdmClient.ico", "UPTON-PLM.ico"):
        ico_image.save(desktop_assets / name, format="ICO", sizes=ico_sizes)


if __name__ == "__main__":
    main()
