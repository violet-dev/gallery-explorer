﻿// This source code is a part of Gallery Explorer Project.
// Copyright (C) 2020. rollrat. Licensed under the MIT Licence.

using OpenCvSharp;
using OpenCvSharp.ImgHash;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GalleryExplorer.Core
{
    public interface IImageSimilarity
    {
        void AppendImage(string path);
        List<List<string>> Clustering(Action<(int, int)> progress = null, int depth = -1, double threshold = -1);
    }

    /// <summary>
    /// 완전히 동일한 이미지들을 찾기위한 메서드입니다. Crc32 해싱
    /// </summary>
    public class ImageEqualSimilarity : IImageSimilarity
    {
        Dictionary<string, List<string>> hashs = new Dictionary<string, List<string>>();

        /// <summary>
        /// https://github.com/damieng/DamienGKit/blob/master/CSharp/DamienG.Library/Security/Cryptography/Crc32.cs
        /// </summary>
        public sealed class Crc32 : HashAlgorithm
        {
            public const UInt32 DefaultPolynomial = 0xedb88320u;
            public const UInt32 DefaultSeed = 0xffffffffu;

            static UInt32[] defaultTable;

            readonly UInt32 seed;
            readonly UInt32[] table;
            UInt32 hash;

            public Crc32()
                : this(DefaultPolynomial, DefaultSeed)
            {
            }

            public Crc32(UInt32 polynomial, UInt32 seed)
            {
                if (!BitConverter.IsLittleEndian)
                    throw new PlatformNotSupportedException("Not supported on Big Endian processors");

                table = InitializeTable(polynomial);
                this.seed = hash = seed;
            }

            public override void Initialize()
            {
                hash = seed;
            }

            protected override void HashCore(byte[] array, int ibStart, int cbSize)
            {
                hash = CalculateHash(table, hash, array, ibStart, cbSize);
            }

            protected override byte[] HashFinal()
            {
                var hashBuffer = UInt32ToBigEndianBytes(~hash);
                HashValue = hashBuffer;
                return hashBuffer;
            }

            public override int HashSize { get { return 32; } }

            public static UInt32 Compute(byte[] buffer)
            {
                return Compute(DefaultSeed, buffer);
            }

            public static UInt32 Compute(UInt32 seed, byte[] buffer)
            {
                return Compute(DefaultPolynomial, seed, buffer);
            }

            public static UInt32 Compute(UInt32 polynomial, UInt32 seed, byte[] buffer)
            {
                return ~CalculateHash(InitializeTable(polynomial), seed, buffer, 0, buffer.Length);
            }

            static UInt32[] InitializeTable(UInt32 polynomial)
            {
                if (polynomial == DefaultPolynomial && defaultTable != null)
                    return defaultTable;

                var createTable = new UInt32[256];
                for (var i = 0; i < 256; i++)
                {
                    var entry = (UInt32)i;
                    for (var j = 0; j < 8; j++)
                        if ((entry & 1) == 1)
                            entry = (entry >> 1) ^ polynomial;
                        else
                            entry >>= 1;
                    createTable[i] = entry;
                }

                if (polynomial == DefaultPolynomial)
                    defaultTable = createTable;

                return createTable;
            }

            static UInt32 CalculateHash(UInt32[] table, UInt32 seed, IList<byte> buffer, int start, int size)
            {
                var hash = seed;
                for (var i = start; i < start + size; i++)
                    hash = (hash >> 8) ^ table[buffer[i] ^ hash & 0xff];
                return hash;
            }

            static byte[] UInt32ToBigEndianBytes(UInt32 uint32)
            {
                var result = BitConverter.GetBytes(uint32);

                if (BitConverter.IsLittleEndian)
                    Array.Reverse(result);

                return result;
            }
        }

        public void AppendImage(string path)
        {
            Crc32 crc32 = new Crc32();
            String hash = String.Empty;

            using (FileStream fs = File.Open(path, FileMode.Open))
                foreach (byte b in crc32.ComputeHash(fs)) hash += b.ToString("x2").ToLower();

            //using (FileStream stream = File.OpenRead(path))
            //{
            //    SHA512Managed sha = new SHA512Managed();
            //    byte[] hash = sha.ComputeHash(stream);
            //    var hashtxt = BitConverter.ToString(hash).Replace("-", String.Empty);
            //}

            lock (hashs)
            {
                if (!hashs.ContainsKey(hash))
                    hashs.Add(hash, new List<string>());
                hashs[hash].Add(path);
            }
        }

        public List<List<string>> Clustering(Action<(int, int)> progress = null, int depth = -1, double threshold = -1)
        {
            return hashs.Select(x => x.Value).ToList();
        }
    }

    /// <summary>
    /// 매우 유사한 이미지들을 찾기위한 메서드입니다. OpenCV BMH Mode1 사용
    /// </summary>
    public class ImageSoftSimilarity : IImageSimilarity
    {
        public Dictionary<string, ulong[]> Hashs { get; private set; } = new Dictionary<string, ulong[]>();

        public static ulong[] MakeSoftHash(string path)
        {
            var image = Cv2.ImRead(path);
            var hash = new Mat();
            var algorithm = BlockMeanHash.Create(BlockMeanHashMode.Mode1);
            if (image.Empty())
                return null;
            algorithm.Compute(image, hash);
            image.Dispose();
            algorithm.Dispose();
            hash.GetArray(out byte[] buffer);
            var rr = new ulong[16];
            for (int i = 0; i < 15; i++)
                rr[i] = BitConverter.ToUInt64(buffer, i * 8);
            rr[15] = buffer[120];
            return rr;
        }

        public void AppendImage(string path)
        {
            var rr = MakeSoftHash(path);
            if (rr == null) return;
            lock (Hashs)
                Hashs.Add(path, rr);
        }

        public static byte BitCount(ulong value)
        {
            ulong result = value - ((value >> 1) & 0x5555555555555555UL);
            result = (result & 0x3333333333333333UL) + ((result >> 2) & 0x3333333333333333UL);
            return (byte)(unchecked(((result + (result >> 4)) & 0xF0F0F0F0F0F0F0FUL) * 0x101010101010101UL) >> 56);
        }

        private static double compute_soft_similarity_vptree((string, ulong[]) s1, (string, ulong[]) s2)
        {
            int result = 0;
            for (int i = 0; i < s1.Item2.Length; i++)
                result += BitCount(s1.Item2[i] ^ s2.Item2[i]);

            return result;
        }

        public List<(string, double)> FindForSoft(ulong[] hash, int counts = 5)
        {
            (string, ulong[])[] outs;
            double[] dist;
            counts = Math.Min(counts, Hashs.Count);
            var vpTree = new VpTree<(string, ulong[])>();
            vpTree.Create(Hashs.Select(x => (x.Key, x.Value)).ToArray(), compute_soft_similarity_vptree);
            vpTree.Search(("", hash), counts, out outs, out dist);
            var result = new List<(string, double)>();
            for (int i = 0; i < outs.Length; i++)
                result.Add((outs[i].Item1, dist[i]));
            return result;
        }

        /// <summary>
        /// Threshold 참고
        /// http://qtandopencv.blogspot.com/2016/06/introduction-to-image-hash-module-of.html
        /// </summary>
        /// <param name="hash"></param>
        /// <param name="threshold"></param>
        public List<List<(string, double)>> Clustering(Action<(int, int)> progress = null, int depth = 50, double threshold = 100)
        {
            if (depth < 0)
                depth = 50;
            if (threshold < 0)
                threshold = 100;

            var vpTree = new VpTree<(string, ulong[])>();
            vpTree.Create(Hashs.Select(x => (x.Key, x.Value)).ToArray(), compute_soft_similarity_vptree);

            var clustered = new HashSet<string>();
            var result = new List<List<(string, double)>>();
            int count = 0;
            foreach (var src in Hashs)
            {
                if (clustered.Contains(src.Key))
                    continue;

                (string, ulong[])[] outs;
                double[] dist;
                var counts = Math.Min(depth, Hashs.Count - count);
                vpTree.Search((src.Key, src.Value), counts, out outs, out dist);

                var sresult = new List<(string, double)>();
                for (int i = 0; i < outs.Length; i++)
                    if (dist[i] < threshold)
                    {
                        sresult.Add((outs[i].Item1, dist[i]));
#if false
                        if (dist[i] < 10)
                        {
                            if (!clustered.Contains(outs[i].Item1))
                            {
                                clustered.Add(outs[i].Item1);
                                count++;
                            }
                        }
#endif
                    }
                sresult.RemoveAll(x => x.Item1 == src.Key);
                sresult.Insert(0, (src.Key, 0));
                result.Add(sresult);
                outs = null;
                dist = null;
                progress?.Invoke((++count, Hashs.Count));
            }

            result.Sort((x, y) => y.Count.CompareTo(x.Count));
            result = result.GroupBy(x =>
            {
                var y = x.Select(z => z.Item1).ToList();
                y.Sort();
                return string.Join("|", y);
            }).Select(x => x.First()).ToList();
            return result;
        }

        List<List<string>> IImageSimilarity.Clustering(Action<(int, int)> progress, int depth, double threshold)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 유사한 이미지들을 찾기 위한 도구입니다. Cosine Distance 사용
    /// </summary>
    public class ImageHardSimilarity : IImageSimilarity
    {
        Dictionary<string, int[,,]> hashs2 = new Dictionary<string, int[,,]>();

        private static int[,,] ConvertToArray(Bitmap bitmap)
        {
            int[,,] convert = new int[3, 30, 30];

            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);

            int width = bitmap.Width;
            int stride = data.Stride;
            int height = bitmap.Height;

            byte[] imageArray = new byte[stride * height];
            Marshal.Copy(data.Scan0, imageArray, 0, imageArray.Length);

            for (int y = 0; y < height; y++)
            {
                int convertY = y * 30 / height;
                for (int x = 0; x < width * 3; x++)
                {
                    int convertX = x * 30 / (width * 3);
                    int rgb = x % 3;
                    int value = imageArray[y * stride + x];
                    convert[rgb, convertY, convertX] += value;
                }
            }

            bitmap.UnlockBits(data);

            return convert;
        }

        public static double GetCosineSimilarity(int[,,] a, int[,,] b)
        {
            double magA2 = 0;
            double magB2 = 0;
            double product = 0;

            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 30; j++)
                {
                    for (int k = 0; k < 30; k++)
                    {
                        magA2 += Math.Pow(a[i, j, k], 2);
                        magB2 += Math.Pow(b[i, j, k], 2);
                        product += (a[i, j, k] * 1.0 * b[i, j, k]);
                    }
                }
            }

            double magAB = Math.Sqrt(magA2 * magB2);
            return product / magAB;
        }

        public int[,,] MakeHash(string path)
        {
            var bitmap = new Bitmap(path);
            var hash = ConvertToArray(bitmap);
            bitmap.Dispose();
            return hash;
        }

        public void AppendImage(string path)
        {
            var bitmap = new Bitmap(path);
            var hash = ConvertToArray(bitmap);
            bitmap.Dispose();
            lock (hashs2)
                hashs2.Add(path, hash);
        }

        private static double compute_hard_similarity_vptree((string, int[,,]) s1, (string, int[,,]) s2)
        {
            return 1 - GetCosineSimilarity(s1.Item2, s2.Item2);
        }

        public List<(string, double)> FindForHard(int[,,] hash, int counts = 5)
        {
            (string, int[,,])[] outs;
            double[] dist;
            counts = Math.Min(counts, hashs2.Count);
            var vpTree = new VpTree<(string, int[,,])>();
            vpTree.Create(hashs2.Select(x => (x.Key, x.Value)).ToArray(), compute_hard_similarity_vptree);
            vpTree.Search(("", hash), counts, out outs, out dist);
            var result = new List<(string, double)>();
            for (int i = 0; i < outs.Length; i++)
                result.Add((outs[i].Item1, dist[i]));
            return result;
        }

        /// <summary>
        /// Threshold 참고
        /// http://qtandopencv.blogspot.com/2016/06/introduction-to-image-hash-module-of.html
        /// </summary>
        /// <param name="hash"></param>
        /// <param name="threshold"></param>
        public List<List<string>> Clustering(Action<(int, int)> progress = null, int depth = 100, double threshold = 0.05)
        {
            if (depth < 0)
                depth = 100;
            if (threshold < 0)
                threshold = 0.05;

            var vpTree = new VpTree<(string, int[,,])>();
            vpTree.Create(hashs2.Select(x => (x.Key, x.Value)).ToArray(), compute_hard_similarity_vptree);

            var clustered = new HashSet<string>();
            var result = new List<List<string>>();
            foreach (var src in hashs2)
            {
                if (clustered.Contains(src.Key))
                    continue;

                (string, int[,,])[] outs;
                double[] dist;
                var counts = Math.Min(depth, hashs2.Count - clustered.Count);
                vpTree.Search((src.Key, src.Value), counts, out outs, out dist);

                var sresult = new List<string>();
                for (int i = 0; i < outs.Length; i++)
                    if (dist[i] < threshold)
                    {
                        sresult.Add(outs[i].Item1);
                        clustered.Add(outs[i].Item1);
                    }
                clustered.Add(src.Key);
                result.Add(sresult);
                progress?.Invoke((clustered.Count, hashs2.Count));
            }

            result.Sort((x, y) => y.Count.CompareTo(x.Count));
            return result;
        }
    }
}
