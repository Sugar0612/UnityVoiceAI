using System.IO;
using UnityEngine;

namespace VoiceAI
{
    /// <summary>
    /// 音频工具：把 AudioClip 转成 WAV 文件字节（16kHz 单声道 16bit PCM，云端识别标准格式）。
    /// </summary>
    public static class WavUtility
    {
        public static byte[] ToWav16kMono(AudioClip clip)
        {
            if (clip == null || clip.samples == 0) return null;

            const int targetRate = 16000;
            float[] data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);

            // 1) 转单声道
            int monoLen = data.Length / clip.channels;
            float[] mono = new float[monoLen];
            for (int i = 0; i < monoLen; i++)
            {
                float sum = 0f;
                for (int c = 0; c < clip.channels; c++) sum += data[i * clip.channels + c];
                mono[i] = sum / clip.channels;
            }

            // 2) 重采样到 16kHz（线性插值）
            float[] resampled;
            if (clip.frequency == targetRate)
            {
                resampled = mono;
            }
            else
            {
                float ratio = (float)clip.frequency / targetRate;
                int outLen = Mathf.CeilToInt(monoLen / ratio);
                resampled = new float[outLen];
                for (int i = 0; i < outLen; i++)
                {
                    float srcPos = i * ratio;
                    int i0 = Mathf.Min((int)srcPos, monoLen - 1);
                    int i1 = Mathf.Min(i0 + 1, monoLen - 1);
                    float t = srcPos - i0;
                    resampled[i] = Mathf.Lerp(mono[i0], mono[i1], t);
                }
            }

            // 3) 写 WAV 头 + PCM16 数据
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int dataSize = resampled.Length * 2;

            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);                    // fmt chunk size
            bw.Write((short)1);              // PCM
            bw.Write((short)1);              // 单声道
            bw.Write(targetRate);
            bw.Write(targetRate * 2);        // byte rate
            bw.Write((short)2);              // block align
            bw.Write((short)16);             // bits per sample
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataSize);

            foreach (float s in resampled)
            {
                short v = (short)(Mathf.Clamp(s, -1f, 1f) * 32767f);
                bw.Write(v);
            }
            bw.Flush();
            return ms.ToArray();
        }
    }
}
