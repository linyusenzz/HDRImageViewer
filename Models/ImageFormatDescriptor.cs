namespace HdrImageViewer.Models;

public sealed record ImageFormatDescriptor(
    string DisplayName,
    HdrImageKind Kind,
    string Decoder,
    string TransferFunction,
    string ColorContainer,
    string SupportStatus);
