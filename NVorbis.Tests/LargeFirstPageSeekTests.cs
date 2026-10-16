using System;
using System.IO;
using Xunit;

namespace NVorbis.Tests
{
    // Regression tests for seeking INTO the first audio page when that page is large.
    //
    // The Vorbis I spec only requires audio to start on a fresh page; it says nothing about
    // how much audio that page may hold.  ffmpeg's Ogg muxer fills the first audio page to
    // its size limit, so at speech bitrates it carries ~1 s of audio (largefirstpage.ogg:
    // mono 44.1 kHz, libvorbis q5, first data page spans granules [0, 44736)).
    //
    // PacketProvider.SeekTo short-circuits targets on the first data page because there is
    // no preceding audio page to validate granule positions against.  An earlier version of
    // that shortcut snapped EVERY such target to the stream start, assuming the page held
    // only ~block_size samples; StreamDecoder then clamped the oversized roll-forward and
    // delivered audio from the wrong position while reporting the requested SamplePosition.
    // The shortcut must still locate the packet containing the target within the page.
    public class LargeFirstPageSeekTests
    {
        private static string TestFile(string name) =>
            Path.Combine(AppContext.BaseDirectory, "TestFiles", name);

        private const string File = "largefirstpage.ogg";
        private const int ProbeLength = 256;

        private static float[] DecodeAll(out int channels)
        {
            using var reader = new VorbisReader(TestFile(File));
            reader.ClipSamples = false;
            channels = reader.Channels;
            var all = new float[reader.TotalSamples * channels];
            var read = 0;
            int count;
            while ((count = reader.ReadSamples(all, read, all.Length - read)) > 0)
            {
                read += count;
            }
            Assert.Equal(all.Length, read);
            return all;
        }

        [Fact]
        public void FirstDataPage_IsLarge_PreconditionForTheseTests()
        {
            // The whole point of the file: a seek target far beyond block_size that still lands
            // on the first data page.  Guards the fixture, not the library.
            using var reader = new VorbisReader(TestFile(File));
            Assert.True(reader.TotalSamples > 44736, "fixture shorter than its first page?");
        }

        [Theory]
        [InlineData(5)]
        [InlineData(640)]
        [InlineData(768)]
        [InlineData(1024)]
        [InlineData(1025)]
        [InlineData(2048)]
        [InlineData(10000)]
        [InlineData(44048)]   // last packets of the first data page
        [InlineData(46048)]   // just past it: the normal FindPacket path, as a control
        public void SeekIntoFirstDataPage_DeliversSamplesFromTheRequestedPosition(long target)
        {
            var linear = DecodeAll(out var channels);

            using var reader = new VorbisReader(TestFile(File));
            reader.ClipSamples = false;
            reader.SeekTo(target, SeekOrigin.Begin);
            Assert.Equal(target, reader.SamplePosition);

            var probe = new float[ProbeLength * channels];
            var got = reader.ReadSamples(probe, 0, probe.Length);
            Assert.Equal(probe.Length, got);

            // sample-exact against the linear decode (seek pre-roll makes the decoder state identical)
            for (var i = 0; i < probe.Length; i++)
            {
                Assert.Equal(linear[target * channels + i], probe[i], 5);
            }
        }

        [Fact]
        public void SeekBackIntoFirstDataPage_AfterReadingFurtherOn_DeliversSamplesFromTheRequestedPosition()
        {
            var linear = DecodeAll(out var channels);

            using var reader = new VorbisReader(TestFile(File));
            reader.ClipSamples = false;
            reader.SeekTo(100_000, SeekOrigin.Begin);
            var scratch = new float[4096 * channels];
            reader.ReadSamples(scratch, 0, scratch.Length);

            const long target = 1024;
            reader.SeekTo(target, SeekOrigin.Begin);
            Assert.Equal(target, reader.SamplePosition);

            var probe = new float[ProbeLength * channels];
            Assert.Equal(probe.Length, reader.ReadSamples(probe, 0, probe.Length));
            for (var i = 0; i < probe.Length; i++)
            {
                Assert.Equal(linear[target * channels + i], probe[i], 5);
            }
        }
    }
}