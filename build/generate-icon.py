"""文件用途：生成与 SVG 一致的应用图标及 Windows 多分辨率 ICO。依赖 Pillow。"""

from pathlib import Path
from PIL import Image, ImageDraw

DESIGN_SIZE = 256
SUPERSAMPLE = 4
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
ASSET_DIRECTORY = Path(__file__).resolve().parents[1] / "src" / "FlashFind.Desktop" / "Assets"
INK = "#202226"
TILE = "#FFFFFF"
OUTLINE = "#DDDFE3"

SVG = '''<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <rect x="8" y="8" width="240" height="240" rx="54" fill="#fff" stroke="#dddfe3" stroke-width="3"/>
  <path d="M48 96V75Q48 67 56 67H96L114 86H192Q200 86 200 94V171Q200 180 191 180H57Q48 180 48 171Z"
        fill="#fff" stroke="#202226" stroke-width="13" stroke-linejoin="round"/>
  <path d="M50 108H197" fill="none" stroke="#202226" stroke-width="11" stroke-linecap="round"/>
  <circle cx="149" cy="147" r="37" fill="#fff" stroke="#fff" stroke-width="20"/>
  <path d="M176 174L207 205" fill="none" stroke="#fff" stroke-width="28" stroke-linecap="round"/>
  <circle cx="149" cy="147" r="35" fill="#fff" stroke="#202226" stroke-width="13"/>
  <path d="M176 174L207 205" fill="none" stroke="#202226" stroke-width="16" stroke-linecap="round"/>
</svg>
'''


def generate_icon() -> None:
    ASSET_DIRECTORY.mkdir(parents=True, exist_ok=True)
    scale = SUPERSAMPLE
    canvas = Image.new("RGBA", (DESIGN_SIZE * scale, DESIGN_SIZE * scale), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)

    def box(bounds: tuple[int, int, int, int]) -> tuple[int, int, int, int]:
        left, top, right, bottom = bounds
        return left * scale, top * scale, right * scale, bottom * scale

    def line(points: list[tuple[int, int]], color: str, width: int) -> None:
        scaled = [(x * scale, y * scale) for x, y in points]
        draw.line(scaled, fill=color, width=width * scale, joint="curve")
        radius = width * scale / 2
        for x, y in scaled:
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)

    draw.rounded_rectangle(box((8, 8, 248, 248)), radius=54 * scale, fill=TILE, outline=OUTLINE, width=3 * scale)
    folder = [(48, 96), (48, 75), (56, 67), (96, 67), (114, 86), (192, 86), (200, 94),
              (200, 171), (191, 180), (57, 180), (48, 171), (48, 96)]
    draw.polygon([(x * scale, y * scale) for x, y in folder], fill=TILE)
    line(folder, INK, 13)
    line([(50, 108), (197, 108)], INK, 11)
    draw.ellipse(box((102, 100, 196, 194)), fill=TILE)
    line([(176, 174), (207, 205)], TILE, 28)
    draw.ellipse(box((107, 105, 191, 189)), fill=INK)
    draw.ellipse(box((120, 118, 178, 176)), fill=TILE)
    line([(176, 174), (207, 205)], INK, 16)
    image = canvas.resize((DESIGN_SIZE, DESIGN_SIZE), Image.Resampling.LANCZOS)
    image.save(ASSET_DIRECTORY / "FileSearch.png")
    image.save(ASSET_DIRECTORY / "FileSearch.ico", format="ICO", sizes=[(size, size) for size in ICON_SIZES])
    (ASSET_DIRECTORY / "FileSearch.svg").write_text(SVG, encoding="utf-8")


if __name__ == "__main__":
    generate_icon()
