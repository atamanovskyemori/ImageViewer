using ImageViewer.Animation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Animation
{
    [TestClass]
    public class AnimationTimelineTests
    {
        private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

        // 3 frames: 100ms, 200ms, 300ms (total 600ms)
        private static AnimationTimeline Create(int loopCount = 0) =>
            new(new[] { Ms(100), Ms(200), Ms(300) }, loopCount);

        [TestMethod]
        public void Starts_At_First_Frame()
        {
            var timeline = Create();

            Assert.AreEqual(0, timeline.Index);
            Assert.AreEqual(3, timeline.FrameCount);
            Assert.AreEqual(Ms(100), timeline.RemainingInFrame);
            Assert.IsFalse(timeline.IsFinished);
        }

        [TestMethod]
        public void Advance_Within_Frame_Only_Reduces_Remaining()
        {
            var timeline = Create();

            Assert.IsFalse(timeline.Advance(Ms(40)));
            Assert.AreEqual(0, timeline.Index);
            Assert.AreEqual(Ms(60), timeline.RemainingInFrame);
        }

        [TestMethod]
        public void Advance_Exactly_Delay_Moves_To_Next_Frame()
        {
            var timeline = Create();

            Assert.IsTrue(timeline.Advance(Ms(100)));
            Assert.AreEqual(1, timeline.Index);
            Assert.AreEqual(Ms(200), timeline.RemainingInFrame);
        }

        [TestMethod]
        public void Late_Advance_Skips_Frames_To_Catch_Up()
        {
            var timeline = Create();

            // 100 (frame 0) + 200 (frame 1) + 50 into frame 2
            Assert.IsTrue(timeline.Advance(Ms(350)));
            Assert.AreEqual(2, timeline.Index);
            Assert.AreEqual(Ms(250), timeline.RemainingInFrame);
        }

        [TestMethod]
        public void Infinite_Loop_Wraps_To_First_Frame()
        {
            var timeline = Create(loopCount: 0);

            timeline.Advance(Ms(600));

            Assert.AreEqual(0, timeline.Index);
            Assert.IsFalse(timeline.IsFinished);
        }

        [TestMethod]
        public void Infinite_Loop_With_Huge_Elapsed_Stays_Valid()
        {
            var timeline = Create(loopCount: 0);

            // About 11.5 days: must not take forever, and lands on a valid frame
            timeline.Advance(TimeSpan.FromSeconds(1_000_000.35));

            Assert.IsTrue(timeline.Index is >= 0 and < 3);
            Assert.IsTrue(timeline.RemainingInFrame > TimeSpan.Zero);
        }

        [TestMethod]
        public void Play_Once_Stops_On_Last_Frame()
        {
            var timeline = Create(loopCount: 1);

            timeline.Advance(Ms(10_000));

            Assert.IsTrue(timeline.IsFinished);
            Assert.AreEqual(2, timeline.Index, "Should stop on the last frame");
            Assert.IsFalse(timeline.Advance(Ms(100)), "Finished timeline does not move");
        }

        [TestMethod]
        public void Play_Twice_Then_Stop()
        {
            var timeline = Create(loopCount: 2);

            timeline.Advance(Ms(600)); // first play done -> back to frame 0
            Assert.AreEqual(0, timeline.Index);
            Assert.IsFalse(timeline.IsFinished);

            timeline.Advance(Ms(599)); // still inside second play
            Assert.IsFalse(timeline.IsFinished);

            timeline.Advance(Ms(1));
            Assert.IsTrue(timeline.IsFinished);
            Assert.AreEqual(2, timeline.Index);
        }

        [TestMethod]
        public void Step_Wraps_Around_And_Resets_Remaining()
        {
            var timeline = Create();

            timeline.Step(-1);
            Assert.AreEqual(2, timeline.Index);
            Assert.AreEqual(Ms(300), timeline.RemainingInFrame);

            timeline.Step(+1);
            Assert.AreEqual(0, timeline.Index);
        }

        [TestMethod]
        public void Step_Clears_Finished()
        {
            var timeline = Create(loopCount: 1);
            timeline.Advance(Ms(10_000));
            Assert.IsTrue(timeline.IsFinished);

            timeline.Step(-1);

            Assert.IsFalse(timeline.IsFinished);
            Assert.AreEqual(1, timeline.Index);
        }

        [TestMethod]
        public void Restart_Goes_Back_To_Start_And_Resets_Loops()
        {
            var timeline = Create(loopCount: 1);
            timeline.Advance(Ms(10_000));

            timeline.Restart();

            Assert.AreEqual(0, timeline.Index);
            Assert.IsFalse(timeline.IsFinished);
            Assert.AreEqual(Ms(100), timeline.RemainingInFrame);

            // Plays once more after restart
            timeline.Advance(Ms(599));
            Assert.IsFalse(timeline.IsFinished);
        }

        [TestMethod]
        public void Zero_Or_Negative_Advance_Does_Nothing()
        {
            var timeline = Create();

            Assert.IsFalse(timeline.Advance(TimeSpan.Zero));
            Assert.IsFalse(timeline.Advance(Ms(-50)));
            Assert.AreEqual(Ms(100), timeline.RemainingInFrame);
        }

        [TestMethod]
        public void Zero_Delay_Frame_Does_Not_Hang()
        {
            var timeline = new AnimationTimeline(new[] { TimeSpan.Zero, TimeSpan.Zero }, 0);

            timeline.Advance(Ms(5));

            Assert.IsTrue(timeline.Index is 0 or 1);
        }

        [TestMethod]
        public void Empty_Delays_Throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new AnimationTimeline(Array.Empty<TimeSpan>(), 0));
        }
    }
}
