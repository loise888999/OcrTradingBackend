namespace OcrTradingBackend.Models;

public sealed record GameWindowResponse(
    long Handle,
    string ProcessName,
    string Title,
    int Left,
    int Top,
    int Width,
    int Height
)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}