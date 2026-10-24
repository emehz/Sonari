/*++

Copyright (c) Sonari contributors. Licensed under the MS-PL (see driver/LICENSE).

Module Name:

    LoopbackBuffer.h

Abstract:

    Ring buffer that connects the render endpoint (Sonari Virtual Output) with the
    capture endpoint (Sonari Virtual Microphone).

    Frames are addressed by an absolute frame index derived from the performance
    counter (see FramesFromQpc). Both streams already advance their positions with
    that same clock, so the render stream writes the frames it "plays" at their
    index and the capture stream reads the frames CaptureDelayFrames() older.
    There is no drift to compensate between the two sides.

    Frames that the render stream did not write (it was stopped, paused or late)
    read back as silence: gaps are zero-filled when writing resumes and anything
    older than one buffer length is treated as overwritten.

    Samples are stored as 32-bit signed integers, stereo interleaved. Only PCM
    16-bit and 32-bit at 48 kHz stereo are supported, which is all the wave
    filters of this driver expose.

    All methods except Init are callable at IRQL <= DISPATCH_LEVEL.
--*/

#ifndef _SONARI_LOOPBACKBUFFER_H_
#define _SONARI_LOOPBACKBUFFER_H_

class CLoopbackBuffer
{
public:
    static const ULONG SampleRate         = 48000;
    static const ULONG Channels           = 2;
    static const ULONG CapacityFrames     = SampleRate;              // 1 second.

    // Capture delay, configurable with the LoopbackDelayMs registry value (see README).
    static const ULONG DefaultDelayMs     = 30;
    static const ULONG MinDelayMs         = 5;
    static const ULONG MaxDelayMs         = 500;

    CLoopbackBuffer();
    ~CLoopbackBuffer();

    // DelayMs is clamped to [MinDelayMs, MaxDelayMs].
    _IRQL_requires_max_(PASSIVE_LEVEL)
    NTSTATUS Init
    (
        _In_ ULONG DelayMs
    );

    ULONG CaptureDelayFrames() const
    {
        return m_CaptureDelayFrames;
    }

    // The render stream reports when it starts and stops running, so that a
    // capture can tell "the render stream is late" from "nothing is playing".
    _IRQL_requires_max_(DISPATCH_LEVEL)
    VOID RenderStarted
    (
        _In_ ULONGLONG FirstFrame
    );

    _IRQL_requires_max_(DISPATCH_LEVEL)
    VOID RenderStopped();

    // Absolute frame index (at SampleRate) of the given performance counter value.
    static ULONGLONG FramesFromQpc
    (
        _In_ LARGE_INTEGER Qpc,
        _In_ LARGE_INTEGER QpcFrequency
    );

    // TRUE if a stream with this format can be connected to the loopback.
    static BOOLEAN IsSupportedFormat
    (
        _In_ const WAVEFORMATEX * Format
    );

    // Stores Frames frames starting at absolute index FirstFrame. When Silence is
    // TRUE (protected content), zeros are stored instead of Data.
    _IRQL_requires_max_(DISPATCH_LEVEL)
    VOID Write
    (
        _In_ ULONGLONG                                      FirstFrame,
        _In_reads_bytes_(Frames * Channels * BitsPerSample / 8) const BYTE * Data,
        _In_ ULONG                                          Frames,
        _In_ USHORT                                         BitsPerSample,
        _In_ BOOLEAN                                        Silence
    );

    // Copies Frames frames starting at absolute index FirstFrame into Data,
    // converted to BitsPerSample. Frames that are not available read as silence.
    // Returns how many of them were missing while the render stream was running:
    // audio that arrived too late for the capture delay (a glitch).
    _IRQL_requires_max_(DISPATCH_LEVEL)
    ULONG Read
    (
        _In_ ULONGLONG                                      FirstFrame,
        _Out_writes_bytes_(Frames * Channels * BitsPerSample / 8) BYTE * Data,
        _In_ ULONG                                          Frames,
        _In_ USHORT                                         BitsPerSample
    );

private:
    KSPIN_LOCK  m_Lock;
    LONG *      m_pSamples;      // CapacityFrames * Channels samples.
    ULONGLONG   m_WrittenEnd;    // One past the newest frame index written so far.
    ULONG       m_CaptureDelayFrames;
    BOOLEAN     m_RenderRunning;
    ULONGLONG   m_RenderStartFrame;  // First frame of the current render run.

    LONG * Slot(_In_ ULONGLONG Frame)
    {
        return m_pSamples + (Frame % CapacityFrames) * Channels;
    }
};

typedef CLoopbackBuffer *PCLoopbackBuffer;

#endif // _SONARI_LOOPBACKBUFFER_H_
