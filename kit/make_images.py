"""Icons for the CS2UIKit Workshop addon, drawn in code so they stay consistent and editable.

White glyphs on transparency, 128x128. The coloured square behind them is the stylesheet's job, and the glyph is
re-tinted with wash-color where the background is light (the yellow warning), so one PNG serves every style.
Every PNG gets the .vtex descriptor the CS2 resource compiler needs next to it.

Run: python kit/make_images.py  (needs Pillow)
"""
import io
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "images")
ADDON_DIR = "panorama/styles/custom_game/cs2uikit"   # where the PNGs live inside the addon
S = 128          # canvas
W = 13           # stroke width

FONT = None
for candidate in ("C:/Windows/Fonts/arialbd.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"):
    if os.path.exists(candidate):
        FONT = candidate
        break


def canvas():
    img = Image.new("RGBA", (S * 4, S * 4), (0, 0, 0, 0))   # drawn 4x and scaled down: smooth edges
    return img, ImageDraw.Draw(img)


def save(img, name):
    img = img.resize((S, S), Image.LANCZOS)
    os.makedirs(OUT, exist_ok=True)
    img.save(os.path.join(OUT, name + ".png"))
    with io.open(os.path.join(OUT, name + ".vtex"), "w", encoding="utf-8", newline="\n") as f:
        f.write(VTEX.replace("{FILE}", f"{ADDON_DIR}/{name}.png"))


def text_glyph(char, name, size=300, dy=0):
    img, d = canvas()
    font = ImageFont.truetype(FONT, size)
    box = d.textbbox((0, 0), char, font=font)
    x = (S * 4 - (box[2] - box[0])) / 2 - box[0]
    y = (S * 4 - (box[3] - box[1])) / 2 - box[1] + dy
    d.text((x, y), char, font=font, fill=(255, 255, 255, 255))
    save(img, name)


def check():
    img, d = canvas()
    d.line([(118, 262), (212, 356), (394, 158)], fill=(255, 255, 255, 255), width=W * 4, joint="curve")
    save(img, "icon_success")


def cross():
    img, d = canvas()
    for a, b in (((142, 142), (370, 370)), ((370, 142), (142, 370))):
        d.line([a, b], fill=(255, 255, 255, 255), width=W * 4)
    save(img, "icon_danger")


def dot():
    img, d = canvas()
    d.ellipse((186, 186, 326, 326), fill=(255, 255, 255, 255))
    save(img, "icon_neutral")


VTEX = '''<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
"CDmeVtex"
{
\t"m_inputTextureArray" "element_array"
\t[
\t\t"CDmeInputTexture"
\t\t{
\t\t\t"m_name" "string" "InputTexture0"
\t\t\t"m_fileName" "string" "{FILE}"
\t\t\t"m_colorSpace" "string" "srgb"
\t\t\t"m_typeString" "string" "2D"
\t\t\t"m_imageProcessorArray" "element_array"
\t\t\t[
\t\t\t\t"CDmeImageProcessor"
\t\t\t\t{
\t\t\t\t\t"m_algorithm" "string" "None"
\t\t\t\t\t"m_stringArg" "string" ""
\t\t\t\t\t"m_vFloat4Arg" "vector4" "0 0 0 0"
\t\t\t\t}
\t\t\t]
\t\t}
\t]
\t"m_outputTypeString" "string" "2D"
\t"m_outputFormat" "string" "BGRA8888"
\t"m_outputClearColor" "vector4" "0 0 0 0"
\t"m_nOutputMinDimension" "int" "0"
\t"m_nOutputMaxDimension" "int" "0"
\t"m_textureOutputChannelArray" "element_array"
\t[
\t\t"CDmeTextureOutputChannel"
\t\t{
\t\t\t"m_inputTextureArray" "string_array" [ "InputTexture0" ]
\t\t\t"m_srcChannels" "string" "rgba"
\t\t\t"m_dstChannels" "string" "rgba"
\t\t\t"m_mipAlgorithm" "CDmeImageProcessor"
\t\t\t{
\t\t\t\t"m_algorithm" "string" "Box"
\t\t\t\t"m_stringArg" "string" ""
\t\t\t\t"m_vFloat4Arg" "vector4" "0 0 0 0"
\t\t\t}
\t\t\t"m_outputColorSpace" "string" "srgb"
\t\t}
\t]
}
'''

if __name__ == "__main__":
    text_glyph("i", "icon_info", size=330)
    text_glyph("!", "icon_warning", size=330)
    check()
    cross()
    dot()
    print("ok:", sorted(f for f in os.listdir(OUT) if f.endswith(".png")))
