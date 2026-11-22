package com.sonictopography.audio;

import android.media.AudioFormat;
import android.media.MediaCodec;
import android.media.MediaExtractor;
import android.media.MediaFormat;
import android.util.Log;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;

public class SonicAndroidAudioDecoder {
    private static final String TAG = "SonicAudioDecoder";
    private static final int ENCODING_PCM_16BIT = AudioFormat.ENCODING_PCM_16BIT; // 2
    private static final int ENCODING_PCM_FLOAT = AudioFormat.ENCODING_PCM_FLOAT; // 4
    private static final int ENCODING_PCM_24BIT_PACKED = 21;
    private static final int ENCODING_PCM_32BIT = 22;

    public static boolean decodeToStereoWav(String inputPath, String outputWavPath) {
        MediaExtractor extractor = null;
        MediaCodec codec = null;
        BufferedOutputStream bos = null;

        try {
            File inFile = new File(inputPath);
            if (!inFile.exists() || inFile.length() < 64) {
                Log.w(TAG, "Input file does not exist or is too small: " + inputPath);
                return false;
            }

            extractor = new MediaExtractor();
            extractor.setDataSource(inputPath);

            int audioTrackIndex = -1;
            MediaFormat trackFormat = null;
            String mime = null;

            int trackCount = extractor.getTrackCount();
            for (int i = 0; i < trackCount; i++) {
                MediaFormat fmt = extractor.getTrackFormat(i);
                if (fmt.containsKey(MediaFormat.KEY_MIME)) {
                    String m = fmt.getString(MediaFormat.KEY_MIME);
                    if (m != null && m.startsWith("audio/")) {
                        audioTrackIndex = i;
                        trackFormat = fmt;
                        mime = m;
                        break;
                    }
                }
            }

            if (audioTrackIndex < 0 || trackFormat == null || mime == null) {
                Log.w(TAG, "No audio track found in: " + inputPath);
                return false;
            }

            extractor.selectTrack(audioTrackIndex);

            int sampleRate = trackFormat.containsKey(MediaFormat.KEY_SAMPLE_RATE)
                    ? trackFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                    : 44100;
            int channels = trackFormat.containsKey(MediaFormat.KEY_CHANNEL_COUNT)
                    ? trackFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    : 2;
            int pcmEncoding = ENCODING_PCM_16BIT;

            codec = MediaCodec.createDecoderByType(mime);
            try {
                trackFormat.setInteger(MediaFormat.KEY_PCM_ENCODING, ENCODING_PCM_16BIT);
                trackFormat.setInteger("max-output-channel-count", 2);
                trackFormat.setInteger(MediaFormat.KEY_AAC_MAX_OUTPUT_CHANNEL_COUNT, 2);
                codec.configure(trackFormat, null, null, 0);
            } catch (Exception configEx) {
                codec.reset();
                MediaFormat fallbackFmt = extractor.getTrackFormat(audioTrackIndex);
                codec.configure(fallbackFmt, null, null, 0);
            }

            codec.start();

            File outFile = new File(outputWavPath);
            File parentDir = outFile.getParentFile();
            if (parentDir != null && !parentDir.exists()) {
                parentDir.mkdirs();
            }

            bos = new BufferedOutputStream(new FileOutputStream(outFile), 65536);
            // Write 44-byte placeholder for WAV header
            byte[] headerPlaceholder = new byte[44];
            bos.write(headerPlaceholder, 0, 44);

            MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
            boolean inputDone = false;
            boolean outputDone = false;
            long totalPcmBytesWritten = 0;

            byte[] rawChunk = new byte[65536];
            byte[] stereoOutChunk = new byte[65536];
            final long timeoutUs = 10000L;

            while (!outputDone) {
                if (!inputDone) {
                    int inputBufIndex = codec.dequeueInputBuffer(timeoutUs);
                    if (inputBufIndex >= 0) {
                        ByteBuffer inputBuf = codec.getInputBuffer(inputBufIndex);
                        if (inputBuf != null) {
                            inputBuf.clear();
                            int sampleSize = extractor.readSampleData(inputBuf, 0);
                            if (sampleSize < 0) {
                                codec.queueInputBuffer(inputBufIndex, 0, 0, 0L, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                                inputDone = true;
                            } else {
                                long presentationTimeUs = extractor.getSampleTime();
                                codec.queueInputBuffer(inputBufIndex, 0, sampleSize, presentationTimeUs, 0);
                                extractor.advance();
                            }
                        }
                    }
                }

                int outIndex = codec.dequeueOutputBuffer(bufferInfo, timeoutUs);
                if (outIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    MediaFormat outFmt = codec.getOutputFormat();
                    if (outFmt.containsKey(MediaFormat.KEY_SAMPLE_RATE)) {
                        sampleRate = outFmt.getInteger(MediaFormat.KEY_SAMPLE_RATE);
                    }
                    if (outFmt.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) {
                        channels = Math.max(1, outFmt.getInteger(MediaFormat.KEY_CHANNEL_COUNT));
                    }
                    if (outFmt.containsKey(MediaFormat.KEY_PCM_ENCODING)) {
                        pcmEncoding = outFmt.getInteger(MediaFormat.KEY_PCM_ENCODING);
                    }
                } else if (outIndex >= 0) {
                    if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                        bufferInfo.size = 0;
                    }

                    if (bufferInfo.size > 0) {
                        ByteBuffer outBuf = codec.getOutputBuffer(outIndex);
                        if (outBuf != null) {
                            outBuf.position(bufferInfo.offset);
                            outBuf.limit(bufferInfo.offset + bufferInfo.size);
                            outBuf.order(ByteOrder.LITTLE_ENDIAN);

                            int written = writeConvertedStereoPcm16(
                                    outBuf,
                                    bufferInfo.size,
                                    channels,
                                    pcmEncoding,
                                    bos,
                                    rawChunk,
                                    stereoOutChunk
                            );
                            totalPcmBytesWritten += written;
                        }
                    }

                    codec.releaseOutputBuffer(outIndex, false);

                    if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                        outputDone = true;
                    }
                }
            }

            bos.flush();
            bos.close();
            bos = null;

            if (totalPcmBytesWritten < 1024) {
                Log.w(TAG, "Decoded PCM too small (" + totalPcmBytesWritten + " bytes) for: " + inputPath);
                outFile.delete();
                return false;
            }

            writeWavHeader(outFile, totalPcmBytesWritten, sampleRate, 2, 16);
            Log.i(TAG, "Decoded " + inputPath + " (" + mime + ", ch=" + channels + ", sr=" + sampleRate + ") -> " + outputWavPath + " (" + totalPcmBytesWritten + " bytes)");
            return true;

        } catch (Exception e) {
            Log.e(TAG, "Error decoding '" + inputPath + "': " + e.getMessage(), e);
            return false;
        } finally {
            if (bos != null) {
                try { bos.close(); } catch (Exception ignored) {}
            }
            if (codec != null) {
                try { codec.stop(); } catch (Exception ignored) {}
                try { codec.release(); } catch (Exception ignored) {}
            }
            if (extractor != null) {
                try { extractor.release(); } catch (Exception ignored) {}
            }
        }
    }

    private static int writeConvertedStereoPcm16(
            ByteBuffer outBuf,
            int byteSize,
            int channels,
            int pcmEncoding,
            BufferedOutputStream bos,
            byte[] rawChunk,
            byte[] stereoOutChunk) throws Exception {

        if (rawChunk.length < byteSize) {
            rawChunk = new byte[byteSize];
        }
        outBuf.get(rawChunk, 0, byteSize);

        // Fast path: already 2-channel 16-bit PCM
        if (pcmEncoding == ENCODING_PCM_16BIT && channels == 2) {
            bos.write(rawChunk, 0, byteSize);
            return byteSize;
        }

        int bytesPerSample;
        if (pcmEncoding == ENCODING_PCM_FLOAT || pcmEncoding == ENCODING_PCM_32BIT) {
            bytesPerSample = 4;
        } else if (pcmEncoding == ENCODING_PCM_24BIT_PACKED) {
            bytesPerSample = 3;
        } else {
            bytesPerSample = 2;
        }

        int bytesPerFrame = bytesPerSample * Math.max(1, channels);
        int frameCount = byteSize / bytesPerFrame;
        if (frameCount <= 0) return 0;

        int outByteCount = frameCount * 4; // 2 channels * 2 bytes per sample
        if (stereoOutChunk.length < outByteCount) {
            stereoOutChunk = new byte[outByteCount];
        }

        int inOffset = 0;
        int outOffset = 0;

        for (int f = 0; f < frameCount; f++) {
            float left = 0f;
            float right = 0f;

            if (channels == 1) {
                float mono = readNormalizedSample(rawChunk, inOffset, pcmEncoding);
                left = mono;
                right = mono;
            } else if (channels == 2) {
                left = readNormalizedSample(rawChunk, inOffset, pcmEncoding);
                right = readNormalizedSample(rawChunk, inOffset + bytesPerSample, pcmEncoding);
            } else if (channels >= 6) {
                // 5.1 / 7.1 surround downmix (FL, FR, FC, LFE, BL, BR) preserving Center vocals and LFE sub-bass
                float fl = readNormalizedSample(rawChunk, inOffset, pcmEncoding);
                float fr = readNormalizedSample(rawChunk, inOffset + bytesPerSample, pcmEncoding);
                float fc = readNormalizedSample(rawChunk, inOffset + bytesPerSample * 2, pcmEncoding);
                float lfe = readNormalizedSample(rawChunk, inOffset + bytesPerSample * 3, pcmEncoding);
                float bl = readNormalizedSample(rawChunk, inOffset + bytesPerSample * 4, pcmEncoding);
                float br = readNormalizedSample(rawChunk, inOffset + bytesPerSample * 5, pcmEncoding);

                left = (fl + 0.7071f * fc + 0.55f * lfe + 0.7071f * bl) * 0.58f;
                right = (fr + 0.7071f * fc + 0.55f * lfe + 0.7071f * br) * 0.58f;
            } else {
                left = readNormalizedSample(rawChunk, inOffset, pcmEncoding);
                right = readNormalizedSample(rawChunk, inOffset + bytesPerSample, pcmEncoding);
            }

            short sLeft = floatToShortPcm(left);
            short sRight = floatToShortPcm(right);

            stereoOutChunk[outOffset] = (byte) (sLeft & 0xFF);
            stereoOutChunk[outOffset + 1] = (byte) ((sLeft >> 8) & 0xFF);
            stereoOutChunk[outOffset + 2] = (byte) (sRight & 0xFF);
            stereoOutChunk[outOffset + 3] = (byte) ((sRight >> 8) & 0xFF);

            inOffset += bytesPerFrame;
            outOffset += 4;
        }

        bos.write(stereoOutChunk, 0, outByteCount);
        return outByteCount;
    }

    private static float readNormalizedSample(byte[] buf, int offset, int pcmEncoding) {
        if (pcmEncoding == ENCODING_PCM_FLOAT) {
            int bits = (buf[offset] & 0xFF)
                    | ((buf[offset + 1] & 0xFF) << 8)
                    | ((buf[offset + 2] & 0xFF) << 16)
                    | ((buf[offset + 3] & 0xFF) << 24);
            return Float.intBitsToFloat(bits);
        } else if (pcmEncoding == ENCODING_PCM_24BIT_PACKED) {
            // Take upper 16 bits as signed short
            short s = (short) ((buf[offset + 1] & 0xFF) | (buf[offset + 2] << 8));
            return s / 32768.0f;
        } else if (pcmEncoding == ENCODING_PCM_32BIT) {
            short s = (short) ((buf[offset + 2] & 0xFF) | (buf[offset + 3] << 8));
            return s / 32768.0f;
        } else {
            short s = (short) ((buf[offset] & 0xFF) | (buf[offset + 1] << 8));
            return s / 32768.0f;
        }
    }

    private static short floatToShortPcm(float v) {
        if (v > 1.0f) v = 1.0f;
        else if (v < -1.0f) v = -1.0f;
        int i = Math.round(v * 32767.0f);
        if (i > 32767) i = 32767;
        else if (i < -32768) i = -32768;
        return (short) i;
    }

    private static void writeWavHeader(File wavFile, long pcmDataLength, int sampleRate, int channels, int bitsPerSample) throws Exception {
        long totalDataLen = pcmDataLength + 36;
        long byteRate = (long) sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        byte[] header = new byte[44];
        header[0] = 'R'; header[1] = 'I'; header[2] = 'F'; header[3] = 'F';
        header[4] = (byte) (totalDataLen & 0xff);
        header[5] = (byte) ((totalDataLen >> 8) & 0xff);
        header[6] = (byte) ((totalDataLen >> 16) & 0xff);
        header[7] = (byte) ((totalDataLen >> 24) & 0xff);
        header[8] = 'W'; header[9] = 'A'; header[10] = 'V'; header[11] = 'E';
        header[12] = 'f'; header[13] = 'm'; header[14] = 't'; header[15] = ' ';
        header[16] = 16; header[17] = 0; header[18] = 0; header[19] = 0;
        header[20] = 1; header[21] = 0; // PCM = 1
        header[22] = (byte) channels; header[23] = 0;
        header[24] = (byte) (sampleRate & 0xff);
        header[25] = (byte) ((sampleRate >> 8) & 0xff);
        header[26] = (byte) ((sampleRate >> 16) & 0xff);
        header[27] = (byte) ((sampleRate >> 24) & 0xff);
        header[28] = (byte) (byteRate & 0xff);
        header[29] = (byte) ((byteRate >> 8) & 0xff);
        header[30] = (byte) ((byteRate >> 16) & 0xff);
        header[31] = (byte) ((byteRate >> 24) & 0xff);
        header[32] = (byte) blockAlign;
        header[33] = 0;
        header[34] = (byte) bitsPerSample;
        header[35] = 0;
        header[36] = 'd'; header[37] = 'a'; header[38] = 't'; header[39] = 'a';
        header[40] = (byte) (pcmDataLength & 0xff);
        header[41] = (byte) ((pcmDataLength >> 8) & 0xff);
        header[42] = (byte) ((pcmDataLength >> 16) & 0xff);
        header[43] = (byte) ((pcmDataLength >> 24) & 0xff);

        try (RandomAccessFile raf = new RandomAccessFile(wavFile, "rw")) {
            raf.seek(0);
            raf.write(header, 0, 44);
        }
    }
}
