namespace MineOS.Application.Dtos;

/// <summary>
/// What a file manager download delivers: the file itself, or a directory as a ZIP.
/// </summary>
/// <param name="FileName">Suggested name for the saved file (a directory gets ".zip").</param>
/// <param name="IsDirectory">True when the download is a ZIP of a directory.</param>
/// <param name="Length">Size in bytes for a file; null for a directory (streamed).</param>
public record FileDownloadDto(string FileName, bool IsDirectory, long? Length, DateTimeOffset Modified);
