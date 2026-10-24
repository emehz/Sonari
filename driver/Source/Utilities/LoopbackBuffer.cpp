/*++

Copyright (c) Sonari contributors. Licensed under the MS-PL (see driver/LICENSE).

Module Name:

    LoopbackBuffer.cpp

Abstract:

    Implementation of the render-to-capture ring buffer. See LoopbackBuffer.h.
--*/

#include "definitions.h"
#include "LoopbackBuffer.h"

#define LOOPBACK_POOLTAG 'BLNS'

//=============================================================================
#pragma code_seg("PAGE")
CLoopbackBuffer::CLoopbackBuffer()
    : m_pSamples(NULL),
      m_WrittenEnd(0)
{
    PAGED_CODE();
    KeInitializeSpinLock(&m_Lock);
}

//=============================================================================
#pragma code_seg("PAGE")
CLoopbackBuffer::~CLoopbackBuffer()
{
    PAGED_CODE();

    if (m_pSamples)
    {
        ExFreePoolWithTag(m_pSamples, LOOPBACK_POOLTAG);
        m_pSamples = NULL;
    }
}

//=============================================================================
#pragma code_seg("PAGE")
_IRQL_requires_max_(PASSIVE_LEVEL)
NTSTATUS CLoopbackBuffer::Init()
{
    PAGED_CODE();

    // ExAllocatePool2 zeroes the allocation, so the buffer starts as silence.
    m_pSamples = (LONG *)ExAllocatePool2(
        POOL_FLAG_NON_PAGED,
        (SIZE_T)CapacityFrames * Channels * sizeof(LONG),
        LOOPBACK_POOLTAG);

    return m_pSamples ? STATUS_SUCCESS : STATUS_INSUFFICIENT_RESOURCES;
}

//=============================================================================
#pragma code_seg()
ULONGLONG CLoopbackBuffer::FramesFromQpc
(
    _In_ LARGE_INTEGER Qpc,
    _In_ LARGE_INTEGER QpcFrequency
)
{
    // Split to avoid overflowing Qpc * SampleRate on long uptimes.
    ULONGLONG ticks     = (ULONGLONG)Qpc.QuadPart;
    ULONGLONG frequency = (ULONGLONG)QpcFrequency.QuadPart;

    return (ticks / frequency) * SampleRate + ((ticks % frequency) * SampleRate) / frequency;
}

//=============================================================================
#pragma code_seg()
BOOLEAN CLoopbackBuffer::IsSupportedFormat
(
    _In_ const WAVEFORMATEX * Format
)
{
    return Format->nSamplesPerSec == SampleRate &&
           Format->nChannels == Channels &&
           (Format->wBitsPerSample == 16 || Format->wBitsPerSample == 32) &&
           Format->nBlockAlign == Channels * Format->wBitsPerSample / 8;
}

//=============================================================================
#pragma code_seg()
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID CLoopbackBuffer::Write
(
    _In_ ULONGLONG      FirstFrame,
    _In_reads_bytes_(Frames * Channels * BitsPerSample / 8) const BYTE * Data,
    _In_ ULONG          Frames,
    _In_ USHORT         BitsPerSample,
    _In_ BOOLEAN        Silence
)
{
    if (m_pSamples == NULL || Frames == 0)
    {
        return;
    }

    // Only the newest CapacityFrames frames can be kept.
    if (Frames > CapacityFrames)
    {
        ULONG skipped = Frames - CapacityFrames;
        Data       += (SIZE_T)skipped * Channels * (BitsPerSample / 8);
        FirstFrame += skipped;
        Frames      = CapacityFrames;
    }

    ULONGLONG end = FirstFrame + Frames;

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    // Zero the frames between the previous write and this one, so a capture
    // never reads audio left over from an earlier pass around the ring.
    if (FirstFrame > m_WrittenEnd)
    {
        ULONGLONG gapStart = m_WrittenEnd;
        if (end > CapacityFrames && gapStart < end - CapacityFrames)
        {
            gapStart = end - CapacityFrames;
        }
        // At most CapacityFrames frames: one or two contiguous runs of the ring.
        ULONG remaining = (ULONG)(FirstFrame - gapStart);
        ULONG position  = (ULONG)(gapStart % CapacityFrames);
        while (remaining > 0)
        {
            ULONG run = min(remaining, CapacityFrames - position);
            RtlZeroMemory(m_pSamples + (SIZE_T)position * Channels, (SIZE_T)run * Channels * sizeof(LONG));
            position   = (position + run) % CapacityFrames;
            remaining -= run;
        }
    }

    for (ULONG i = 0; i < Frames; i++)
    {
        LONG * slot = Slot(FirstFrame + i);
        for (ULONG channel = 0; channel < Channels; channel++)
        {
            LONG sample = 0;
            if (!Silence)
            {
                SIZE_T index = (SIZE_T)i * Channels + channel;
                if (BitsPerSample == 16)
                {
                    sample = (LONG)((const SHORT *)Data)[index] * 65536;
                }
                else if (BitsPerSample == 32)
                {
                    sample = ((const LONG *)Data)[index];
                }
            }
            slot[channel] = sample;
        }
    }

    if (end > m_WrittenEnd)
    {
        m_WrittenEnd = end;
    }

    KeReleaseSpinLock(&m_Lock, oldIrql);
}

//=============================================================================
#pragma code_seg()
_IRQL_requires_max_(DISPATCH_LEVEL)
VOID CLoopbackBuffer::Read
(
    _In_ ULONGLONG      FirstFrame,
    _Out_writes_bytes_(Frames * Channels * BitsPerSample / 8) BYTE * Data,
    _In_ ULONG          Frames,
    _In_ USHORT         BitsPerSample
)
{
    if (Frames == 0)
    {
        return;
    }

    if (m_pSamples == NULL)
    {
        RtlZeroMemory(Data, (SIZE_T)Frames * Channels * (BitsPerSample / 8));
        return;
    }

    KIRQL oldIrql;
    KeAcquireSpinLock(&m_Lock, &oldIrql);

    // Valid frames: [m_WrittenEnd - CapacityFrames, m_WrittenEnd).
    ULONGLONG oldest = m_WrittenEnd > CapacityFrames ? m_WrittenEnd - CapacityFrames : 0;

    for (ULONG i = 0; i < Frames; i++)
    {
        ULONGLONG frame = FirstFrame + i;
        BOOLEAN available = frame >= oldest && frame < m_WrittenEnd;
        const LONG * slot = available ? Slot(frame) : NULL;

        for (ULONG channel = 0; channel < Channels; channel++)
        {
            LONG sample = slot ? slot[channel] : 0;
            SIZE_T index = (SIZE_T)i * Channels + channel;
            if (BitsPerSample == 16)
            {
                ((SHORT *)Data)[index] = (SHORT)(sample >> 16);
            }
            else if (BitsPerSample == 32)
            {
                ((LONG *)Data)[index] = sample;
            }
        }
    }

    KeReleaseSpinLock(&m_Lock, oldIrql);
}
