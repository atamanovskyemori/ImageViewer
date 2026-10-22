using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests
{
    [TestClass]
    public class ViewGeometryTests
    {
        private const double Tolerance = 1e-9;

        private static void AssertPoint(Point expected, Point actual)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance, "X");
            Assert.AreEqual(expected.Y, actual.Y, Tolerance, "Y");
        }

        [TestMethod]
        public void Rotated_Size_Swaps_For_90_And_270()
        {
            var size = new Size(400, 300);
            Assert.AreEqual(size, ViewGeometry.Rotated(size, 0));
            Assert.AreEqual(new Size(300, 400), ViewGeometry.Rotated(size, 90));
            Assert.AreEqual(size, ViewGeometry.Rotated(size, 180));
            Assert.AreEqual(new Size(300, 400), ViewGeometry.Rotated(size, 270));
        }

        [TestMethod]
        public void Unrotated_Image_Is_Scaled_Then_Offset()
        {
            var m = ViewGeometry.Build(new Size(400, 300), 0, 2.0, new Vector(10, 20));

            AssertPoint(new Point(10, 20), m.Transform(new Point(0, 0)));
            AssertPoint(new Point(810, 620), m.Transform(new Point(400, 300)));
        }

        [TestMethod]
        public void Rotated_Image_Stays_At_The_Offset()
        {
            // Rotating 90° clockwise: the top-left corner of the image goes to the top-right of the bounding box
            var m = ViewGeometry.Build(new Size(400, 300), 90, 1.0, new Vector(0, 0));

            AssertPoint(new Point(300, 0), m.Transform(new Point(0, 0)));
            AssertPoint(new Point(0, 400), m.Transform(new Point(400, 300)));
        }

        [TestMethod]
        public void Centered_Image_Shows_Its_Middle_At_The_Screen_Center()
        {
            // 400x300 image at 100% in an 800x600 viewport, centered
            var ratio = ViewGeometry.ToImageRatio(new Point(400, 300), new Size(400, 300), 0, 1.0, new Vector(200, 150));

            AssertPoint(new Point(0.5, 0.5), ratio);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(90)]
        [DataRow(180)]
        [DataRow(270)]
        public void OffsetFor_Puts_The_Same_Place_Back_At_The_Screen_Point(int rotation)
        {
            var size = new Size(400, 300);
            var screen = new Point(512, 384);
            var ratio = new Point(0.25, 0.8);

            var offset = ViewGeometry.OffsetFor(ratio, screen, size, rotation, 3.0);

            AssertPoint(ratio, ViewGeometry.ToImageRatio(screen, size, rotation, 3.0, offset));
        }

        [TestMethod]
        public void Same_Place_Is_Kept_On_A_Larger_Image()
        {
            // Looking at the right quarter of a small image, then switching to an image twice as large
            var screen = new Point(400, 300);
            var small = new Size(400, 300);
            var ratio = ViewGeometry.ToImageRatio(screen, small, 0, 2.0, new Vector(-300, -150));
            AssertPoint(new Point(0.875, 0.75), ratio); // (400 + 300) / 2 = 350 of 400, (300 + 150) / 2 = 225 of 300

            var large = new Size(800, 600);
            var offset = ViewGeometry.OffsetFor(ratio, screen, large, 0, 2.0);

            AssertPoint(ratio, ViewGeometry.ToImageRatio(screen, large, 0, 2.0, offset));
        }

        [TestMethod]
        public void Empty_Image_Is_Treated_As_Centered()
        {
            AssertPoint(new Point(0.5, 0.5), ViewGeometry.ToImageRatio(new Point(10, 10), new Size(0, 0), 0, 1.0, default));
        }
    }
}
