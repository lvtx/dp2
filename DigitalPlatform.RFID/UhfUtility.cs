﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DigitalPlatform.RFID
{
    /// <summary>
    /// 超高频标签实用函数
    /// </summary>
    public static class UhfUtility
    {
        #region 编码

        // 将字符翻译为 URN40 对照表中的数值
        static int GetUrn40Decimal(char c)
        {
            if (c >= 'A' && c <= 'Z')
            {
                return c - 'A' + 1;
            }

            if (c == '-')
                return 27;
            if (c == '.')
                return 28;
            if (c == ':')
                return 29;
            if (c >= '0' && c <= '9')
                return c - '0' + 30;

            return 0;   // 表示 c 不在对照表中
        }

        static List<int> GetUrn40Decimal(string text)
        {
            List<int> results = new List<int>();
            foreach (var c in text)
            {
                results.Add(GetUrn40Decimal(c));
            }

            return results;
        }

        // 为编码 URN 40 需要的一个段落
        public class Segment
        {
            // 段类型
            // 为 digit table utf8-one-byte utf8-two-byte utf8-triple-byte 之一
            public string Type { get; set; }
            // 段内文字
            public string Text { get; set; }
        }

        // (为编码 URN 40)把文字切割为若干个段落
        public static List<Segment> SplitSegment(string text)
        {
            List<Segment> results = new List<Segment>();

            List<char> buffer = new List<char>();   // 缓冲区
            string buffer_type = "";    // 为 digit table utf8-one-byte utf8-two-byte utf8-triple-byte 之一
            foreach (char c in text)
            {
                int d = GetUrn40Decimal(c);
                if (d != 0)
                {
                    // *** 表内字符

                    if (char.IsDigit(c) == true)
                    {
                        if (buffer_type != "digit"
                            && buffer.Count > 0)
                        {
                            results.Add(new Segment
                            {
                                Type = buffer_type,
                                Text = new string(buffer.ToArray())
                            });

                            buffer.Clear();
                        }
                        buffer.Add(c);
                        buffer_type = "digit";
                    }
                    else if (char.IsDigit(c) == false)
                    {
                        if (buffer_type != "table"
                            && buffer.Count > 0)
                        {
                            results.Add(new Segment
                            {
                                Type = buffer_type,
                                Text = new string(buffer.ToArray())
                            });

                            buffer.Clear();
                        }
                        buffer.Add(c);
                        buffer_type = "table";
                    }
                    else
                        throw new Exception("不可能到达这里");
                }
                else
                {
                    // *** 非表内字符

                    int count = GetUtf8ByteCount(c);
                    if (count == 1)
                    {
                        if (buffer_type != "utf8-one-byte"
                            && buffer.Count > 0)
                        {
                            results.Add(new Segment
                            {
                                Type = buffer_type,
                                Text = new string(buffer.ToArray())
                            });

                            buffer.Clear();
                        }
                        buffer.Add(c);
                        buffer_type = "utf8-one-byte";
                    }
                    else if (count == 2)
                    {
                        if (buffer_type != "utf8-two-byte"
                            && buffer.Count > 0)
                        {
                            results.Add(new Segment
                            {
                                Type = buffer_type,
                                Text = new string(buffer.ToArray())
                            });

                            buffer.Clear();
                        }
                        buffer.Add(c);
                        buffer_type = "utf8-two-byte";
                    }
                    else if (count == 3)
                    {
                        if (buffer_type != "utf8-triple-byte"
                            && buffer.Count > 0)
                        {
                            results.Add(new Segment
                            {
                                Type = buffer_type,
                                Text = new string(buffer.ToArray())
                            });

                            buffer.Clear();
                        }
                        buffer.Add(c);
                        buffer_type = "utf8-triple-byte";
                    }
                    else
                    {
                        throw new Exception($"无法编码字符 '{c}'，因为它是 UTF-8 {count} bytes 形态");
                    }
                }
            }

            if (buffer.Count > 0)
            {
                results.Add(new Segment
                {
                    Type = buffer_type,
                    Text = new string(buffer.ToArray())
                });
            }

            if (results.Count <= 1 && results[0].Type != "digit")
                return results;

            // 把不足 9 字符的 digit 段落和前后的 table 段落合并
            List<Segment> merged = new List<Segment>();

            Segment prev = null;
            foreach (var segment in results)
            {
                if (segment.Type == "digit"
&& segment.Text.Length < 9)
                    segment.Type = "table";

                if (prev != null)
                {
                    if (segment.Type == "table"
    && prev.Type == "table")
                    {
                        prev = new Segment
                        {
                            Type = "table",
                            Text = prev.Text + segment.Text
                        };
                        continue;
                    }
                    merged.Add(prev);
                    prev = null;
                }
                prev = segment;
            }
            if (prev != null)
                merged.Add(prev);

            // 如果段落为小于 9 字符的 digit 类型，要改为 table 类型
            foreach (var segment in merged)
            {
                if (segment.Type == "digit"
                    && segment.Text.Length < 9)
                    segment.Type = "table";
            }

            return merged;
        }

        public static int GetUtf8ByteCount(char c)
        {
            return Encoding.UTF8.GetByteCount(new char[] { c });
        }

        // 编码 UII
        public static byte[] EncodeUII(string text)
        {
            List<byte> results = new List<byte>();

            var segments = SplitSegment(text);
            foreach (var segment in segments)
            {
                if (segment.Type == "digit")
                    results.AddRange(EncodeLongNumericString(segment.Text));
                else if (segment.Type == "table")
                    results.AddRange(
                        EncodeTableDecimals(GetUrn40Decimal(segment.Text))
                        );
                else if (segment.Type == "utf8-one-byte")
                {
                    foreach (var c in segment.Text)
                    {
                        // ISO 646 输出
                        results.Add(0xfc);
                        results.Add((byte)c);
                    }
                }
                else if (segment.Type == "utf8-two-byte")
                {
                    foreach (var c in segment.Text)
                    {
                        results.Add(0xfd);
                        results.AddRange(Encoding.UTF8.GetBytes(new char[] { c }));
                    }
                }
                else if (segment.Type == "utf8-triple-byte")
                {
                    foreach (var c in segment.Text)
                    {
                        results.Add(0xfe);
                        results.AddRange(Encoding.UTF8.GetBytes(new char[] { c }));
                    }
                }
                else
                    throw new Exception($"无法识别的 segment type '{segment.Type}'");
            }

            return results.ToArray();
        }

        // 将 (URN Code 40) 数值每三个编码为两个 bytes
        public static byte[] EncodeTableDecimals(List<int> chars)
        {
            // 补足到 3 的整倍数
            int delta = 3 - (chars.Count % 3);
            if (delta < 3)
            {
                for (int i = 0; i < delta; i++)
                {
                    chars.Add((char)0);
                }
            }

            List<byte> results = new List<byte>();
            int offs = 0;
            while (offs < chars.Count)
            {
                var c1 = chars[offs++];
                var c2 = chars[offs++];
                var c3 = chars[offs++];

                results.AddRange(EncodeTriple(c1, c2, c3));
            }

            return results.ToArray();
        }

        // 编码范围在 'A'-'9' 内的三个字符为两个 bytes
        public static byte[] EncodeTriple(int c1, int c2, int c3)
        {
            List<byte> results = new List<byte>();
            uint v = (1600 * (uint)c1) + (40 * (uint)c2) + (uint)c3 + 1;
            if (v > UInt16.MaxValue)
                throw new Exception($"{v} 溢出 16 bit 整数范围");
            return Compact.ReverseBytes(BitConverter.GetBytes((UInt16)v));
        }

        // 以 long numeric string 方式编码一个数字字符串
        // 算法可参考：
        // https://www.ipc.be/~/media/documents/public/operations/rfid/ipc%20rfid%20standard%20for%20test%20letters.pdf?la=en
        public static byte[] EncodeLongNumericString(string text)
        {
            if (text.Length < 9)
                throw new ArgumentException($"用于编码的数字字符串不应短于 9 字符(但现在是 {text.Length} 字符)");

            byte[] bytes = Compact.IntegerCompact(text);
            if (bytes.Length < 4)
                throw new ArgumentException($"数字字符串编码后不应短于 4 bytes(但现在是 {bytes.Length} bytes)");

            byte second = (byte)((((text.Length - 9) << 4) & (byte)0xf0) | ((bytes.Length - 4) & 0x0f));

            List<byte> results = new List<byte>();
            results.Add(0xfb);
            results.Add(second);
            results.AddRange(bytes);

            return results.ToArray();
        }

        #endregion

        #region 解码

        // 解析 MB01 数据结构
        // parameters:
        //      data    MB01 包含的数据。注意开头 2 bytes 是校验码，从 0x10 bits 偏移位置开始是协议控制字(PC)
        public static MB01Info ParseMB01(byte[] data)
        {
            MB01Info result = new MB01Info();
            result.PC = ParsePC(data, 2);
            if (result.PC.ISO == true)
            {
                if (result.PC.AFI != 0xc2)
                    throw new Exception("目前仅支持 AFI 为 0xc2 的图书馆应用家族标签");
            }
            else
            {
                throw new Exception("目前暂不支持 GC1/EPC 的 MB01");
            }

            return result;
        }

        // 解析协议控制字(Protocol Control Word)
        public static ProtocolControlWord ParsePC(byte[] data, int start)
        {
            if (data.Length < 2)
                throw new ArgumentException($"data 内应包含至少 2 bytes(但现在只有 {data.Length})", nameof(data));

            ProtocolControlWord result = new ProtocolControlWord();
            // 0x0 1 2 3 4 bits
            result.LengthIndicator = (data[0] >> 3) & 0x1f;
            result.UMI = (data[start] & 0x4) != 0;
            result.XPC = (data[start] & 0x2) != 0;
            result.ISO = (data[start] & 0x1) != 0;
            result.AFI = data[start + 1];
            return result;
        }

        // 解码 UII
        public static string DecodeUII(byte[] data, int start, int length)
        {
            int offs = start;
            StringBuilder result = new StringBuilder();
            while (offs - start < length)
            {
                byte b = data[offs];
                if (b <= 0xfa)
                {
                    // 16 bit 按照 URN40 解释为 3 个字符
                    result.Append(UrnCode40_DecodeOneWord(data, offs));
                    offs += 2;
                }
                else if (b == 0xfb)
                {
                    // long numeric string
                    result.Append(DecodeLongNumericString(data, start, out int used));
                    offs += used;
                }
                else if (b == 0xfc)
                {
                    // next byte is ISO/IEC 646 IRV character
                    result.Append(data[offs + 1]);
                    offs += 2;
                }
                else if (b == 0xfd)
                {
                    string temp = Encoding.UTF8.GetString(data, offs + 1, 2);
                    result.Append(temp);
                    offs += 3;
                }
                else if (b == 0xfe)
                {
                    string temp = Encoding.UTF8.GetString(data, offs + 1, 3);
                    result.Append(temp);
                    offs += 4;
                }
            }

            return result.ToString();
        }

        // 将 URN40 对照表中的数值翻译为字符
        static char GetUrn40Char(uint d)
        {
            if (d >= 1 && d <= 26)
            {
                return (char)((uint)'A' + (d - 1));
            }

            if (d == 27)
                return '-';
            if (d == 28)
                return '.';
            if (d == 29)
                return ':';

            if (d >= 30 && d <= 39)
                return (char)((uint)'0' + (d - 30));

            // 表示 d 不在对照表中
            throw new ArgumentException($"值 {d} 不在 URN40 表中");
        }


        // 解析一个 Word(=2 bytes)
        static string UrnCode40_DecodeOneWord(byte[] data,
            int start)
        {
            if (start >= data.Length - 1)
                return "";

            uint v = ((((uint)data[start]) << 8) & 0xff00) + ((uint)data[start + 1] & 0x00ff);
            // 2020/8/17
            if (v == 0)
                return "";
            /*
            byte[] word = new byte[2];
            word[0] = data[start + 0];
            word[1] = data[start + 1];
            uint v = BitConverter.ToUInt16(word, 0);
            */
            v--;
            uint c1 = v / 1600;
            uint rest = (v % 1600);
            uint c2 = rest / 40;
            uint c3 = (rest % 40);
            List<char> results = new List<char>();
            if (c1 != 0)
                results.Add(GetUrn40Char(c1));
            if (c2 != 0)
                results.Add(GetUrn40Char(c2));
            if (c3 != 0)
                results.Add(GetUrn40Char(c3));
            return new string(results.ToArray());
        }

        // 解析 Long Numeric String
        // 注意 start 位置的 byte 应该是 0xfb
        // parameters:
        //      used_bytes  返回用掉的 bytes 数
        public static string DecodeLongNumericString(byte[] data,
            int start,
            out int used_bytes)
        {
            used_bytes = 0;
            byte lead = data[start];
            if (lead != 0xfb)
                throw new ArgumentException("开始的第一个 byte 必须为 0xfb", $"{nameof(data)}, {nameof(start)}");
            byte second = data[start + 1];
            // 解码后的数字应有的字符数
            int decimal_length = ((second >> 4) & 0x0f) + 9;
            // 编码后的 bytes 数
            int encoded_bytes = (second & 0x0f) + 4;

            if (encoded_bytes > 19)
                throw new Exception($"Long Numeric String 的编码后 bytes 数({encoded_bytes})不合法，应小于等于 19");

            string result = "";
            {
                byte[] bytes = new byte[encoded_bytes];
                Array.Copy(data, start + 2, bytes, 0, encoded_bytes);
                result = Compact.IntegerExtract(bytes);
            }

            // 补齐前方的 '0'
            result.PadLeft(decimal_length, '0');
            used_bytes = encoded_bytes + 2;
            return result;
        }

        #endregion


    }

    public class MB01Info
    {
        // Protocol Control Word
        public ProtocolControlWord PC { get; set; }
        // 
        public string UII { get; set; }
    }

    public class ProtocolControlWord
    {
        // Length Indicator
        public int LengthIndicator { get; set; }
        // User Memory Indicator
        public bool UMI { get; set; }
        // XPC 是否存在？
        public bool XPC { get; set; }
        // 是 ISO AFI 还是 GS1/EPC ?
        public bool ISO { get; set; }
        // 图书馆应用的 AFI 应该为 0xc2
        public int AFI { get; set; }
    }
}