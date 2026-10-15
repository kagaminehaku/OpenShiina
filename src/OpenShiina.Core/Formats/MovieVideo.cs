namespace OpenShiina.Formats;

/// <summary>The picture of a movie: frames in display order (Mpeg1Video, Vc1Video).</summary>
public interface IMovieVideo
{
    int Width { get; }
    int Height { get; }
    /// <summary>Frames a second.</summary>
    double FrameRate { get; }

    /// <summary>The next frame in display order, or null at the end. The frame stays valid until the next call.</summary>
    MpegFrame? Next();

    /// <summary>Back to the first frame.</summary>
    void Rewind();
}
