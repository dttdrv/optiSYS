from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parent
GENERATED_DIR = ROOT / "generated"
APP_ASSETS_DIR = ROOT.parent.parent / "src" / "OptiSYS.App" / "Assets"

WIZARD_MAIN_OUTPUT = GENERATED_DIR / "wizard-main.png"
WIZARD_SMALL_OUTPUT = GENERATED_DIR / "wizard-small.png"
INSTALLER_ICON_OUTPUT = GENERATED_DIR / "SetupIcon.ico"
APP_ICON_OUTPUT = APP_ASSETS_DIR / "AppIcon.ico"
APP_ICON_PNG_OUTPUT = APP_ASSETS_DIR / "AppIcon.png"

ACCENT = (104, 176, 143, 255)
ACCENT_DARK = (38, 82, 68, 255)
BG_TOP = (20, 24, 27, 255)
BG_BOTTOM = (13, 17, 20, 255)
TEXT = (244, 247, 245, 255)
MUTED = (158, 168, 164, 255)


def ensure_dirs() -> None:
    GENERATED_DIR.mkdir(parents=True, exist_ok=True)
    APP_ASSETS_DIR.mkdir(parents=True, exist_ok=True)


def font(size: int, bold: bool = False) -> ImageFont.ImageFont:
    candidates = [
        "C:/Windows/Fonts/segoeuib.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
        "C:/Windows/Fonts/seguisb.ttf",
        "C:/Windows/Fonts/arial.ttf",
    ]
    for path in candidates:
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    return ImageFont.load_default()


def rounded_rect(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], radius: int, fill, outline=None, width=1) -> None:
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def draw_leaf_mark(size: int, transparent: bool = True) -> Image.Image:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0) if transparent else BG_TOP)
    draw = ImageDraw.Draw(image)

    stroke = max(2, round(size * 0.075))
    inset = round(size * 0.22)
    box = (inset, inset, size - inset, size - inset)
    draw.arc(box, start=28, end=332, fill=ACCENT, width=stroke)

    arrow = [
        (round(size * 0.74), round(size * 0.18)),
        (round(size * 0.84), round(size * 0.30)),
        (round(size * 0.69), round(size * 0.32)),
    ]
    draw.polygon(arrow, fill=ACCENT)

    dot = round(size * 0.075)
    center = size // 2
    draw.ellipse(
        (center - dot, center - dot, center + dot, center + dot),
        fill=(232, 239, 235, 255),
    )
    return image


def vertical_gradient(width: int, height: int) -> Image.Image:
    image = Image.new("RGBA", (width, height), BG_TOP)
    pixels = image.load()
    for y in range(height):
        t = y / max(1, height - 1)
        for x in range(width):
            pixels[x, y] = tuple(round(BG_TOP[i] * (1 - t) + BG_BOTTOM[i] * t) for i in range(4))
    return image


def write_icons() -> None:
    app_icon = draw_leaf_mark(512)
    app_icon.save(APP_ICON_PNG_OUTPUT)
    ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    app_icon.save(APP_ICON_OUTPUT, sizes=ico_sizes)
    app_icon.save(INSTALLER_ICON_OUTPUT, sizes=ico_sizes)


def write_wizard_assets() -> None:
    main = vertical_gradient(240, 459)
    draw = ImageDraw.Draw(main)
    rounded_rect(draw, (42, 58, 140, 156), 12, fill=(28, 34, 36, 255), outline=(76, 100, 92, 180), width=1)
    main.alpha_composite(draw_leaf_mark(96), (43, 59))
    draw.text((42, 184), "optiSYS", fill=TEXT, font=font(28, bold=True))
    draw.text((42, 220), "Safe runtime", fill=MUTED, font=font(16))
    draw.text((42, 244), "optimization.", fill=MUTED, font=font(16))

    for index, label in enumerate(("Welcome", "Install", "Finish")):
        y = 330 + index * 48
        fill = ACCENT if index == 0 else (25, 31, 34, 255)
        outline = (116, 130, 124, 180)
        draw.ellipse((42, y, 72, y + 30), fill=fill, outline=outline, width=1)
        draw.text((53, y + 5), str(index + 1), fill=TEXT, font=font(13, bold=True))
        draw.text((86, y + 4), label, fill=TEXT if index == 0 else MUTED, font=font(15))
        if index < 2:
            draw.line((57, y + 34, 57, y + 45), fill=(80, 88, 88, 255), width=1)

    vignette = Image.new("RGBA", main.size, (0, 0, 0, 0))
    vignette_draw = ImageDraw.Draw(vignette)
    vignette_draw.ellipse((-120, -120, 290, 250), fill=(80, 160, 90, 42))
    vignette = vignette.filter(ImageFilter.GaussianBlur(34))
    main.alpha_composite(vignette)
    main.save(WIZARD_MAIN_OUTPUT)

    small = Image.new("RGBA", (147, 147), (18, 22, 25, 255))
    small.alpha_composite(draw_leaf_mark(112), (18, 18))
    small.save(WIZARD_SMALL_OUTPUT)


def main() -> None:
    ensure_dirs()
    write_icons()
    write_wizard_assets()


if __name__ == "__main__":
    main()
