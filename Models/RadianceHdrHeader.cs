namespace HdrImageViewer.Models;

public sealed record RadianceHdrHeader(
    int Width,
    int Height,
    bool IsXyze,
    double Exposure,
    string Format,
    string ResolutionLine);
