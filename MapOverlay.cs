// Debug overlay sharing TunnelDebug.Picture with /map. It follows the camera rather
// than showing the fixed opening range, which makes route problems easier to spot.

using Raylib_cs;
using System.Numerics;

namespace GenesisTunnel;

sealed class MapOverlay
{
    const int Behind = 30, Slices = 230;      // map slices shown behind / in total (2 slices = 1 unit)
    Texture2D texture;

    public void Draw(Tunnel tunnel, Flight flight, Rectangle picture)
    {
        // Regenerating the small CPU image is simpler than maintaining a second GPU
        // representation of the occupancy grid. Reuse the texture between frames.
        int camera = (int)(flight.S / Grid.Slice);
        byte[] px = TunnelDebug.Picture(tunnel.CellState, slice => tunnel.PathAt((slice + 0.5) * Grid.Slice),
                                        camera - Behind, Slices, camera, out int w, out int h);
        unsafe
        {
            fixed (byte* p = px)
            {
                if (texture.Id == 0)
                {
                    var img = new Image { Data = p, Width = w, Height = h, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
                    texture = Raylib.LoadTextureFromImage(img);
                }
                else Raylib.UpdateTexture(texture, p);
            }
        }
        float width = MathF.Round(picture.Width * 0.4f), height = MathF.Round(width * h / w);
        var where = new Rectangle(picture.X + picture.Width - width - 12, picture.Y + 12, width, height);
        Raylib.DrawRectangleRec(where, new Color(0, 0, 0, 150));
        Raylib.DrawTexturePro(texture, new Rectangle(0, 0, w, h), where, Vector2.Zero, 0, new Color(255, 255, 255, 220));
    }
}
