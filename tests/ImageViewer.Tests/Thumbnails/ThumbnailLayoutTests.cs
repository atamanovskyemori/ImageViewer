using ImageViewer.Thumbnails;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Thumbnails
{
    [TestClass]
    public class ThumbnailLayoutTests
    {
        [TestMethod]
        public void Default_Size_Is_One_Of_The_Sizes()
        {
            CollectionAssert.Contains(ThumbnailLayout.Sizes.ToList(), ThumbnailLayout.DefaultSize);
        }

        [TestMethod]
        [DataRow(160, 160)]
        [DataRow(150, 160)]
        [DataRow(0, 64)]
        [DataRow(-5, 64)]
        [DataRow(10000, 256)]
        [DataRow(70, 64)]   // 6 away from 64, 10 away from 80
        public void Normalize_Snaps_To_Nearest_Size(int input, int expected)
        {
            Assert.AreEqual(expected, ThumbnailLayout.Normalize(input));
        }

        [TestMethod]
        public void StepSize_Moves_One_Step_And_Stops_At_Ends()
        {
            Assert.AreEqual(192, ThumbnailLayout.StepSize(160, +1));
            Assert.AreEqual(128, ThumbnailLayout.StepSize(160, -1));
            Assert.AreEqual(256, ThumbnailLayout.StepSize(256, +1));
            Assert.AreEqual(64, ThumbnailLayout.StepSize(64, -1));
        }

        [TestMethod]
        public void Columns_Fit_Whole_Tiles_And_Never_Drop_Below_One()
        {
            double tile = ThumbnailLayout.TileWidth(160);

            Assert.AreEqual(3, ThumbnailLayout.Columns(tile * 3, 160));
            Assert.AreEqual(3, ThumbnailLayout.Columns(tile * 4 - 1, 160));
            Assert.AreEqual(1, ThumbnailLayout.Columns(10, 160));
            Assert.AreEqual(1, ThumbnailLayout.Columns(0, 160));
            Assert.AreEqual(1, ThumbnailLayout.Columns(double.NaN, 160));
        }

        [TestMethod]
        public void VisibleRows_Covers_Partially_Visible_Rows()
        {
            // rows are 100 high; viewport shows y = 150..400 -> rows 1, 2, 3
            Assert.AreEqual((1, 3), ThumbnailLayout.VisibleRows(150, 250, 100, 10));

            // exactly aligned: y = 200..400 -> rows 2, 3
            Assert.AreEqual((2, 3), ThumbnailLayout.VisibleRows(200, 200, 100, 10));

            // clamped to the last row
            Assert.AreEqual((8, 9), ThumbnailLayout.VisibleRows(850, 500, 100, 10));
        }

        [TestMethod]
        public void VisibleRows_Is_Empty_Without_Rows_Or_Viewport()
        {
            var (first, last) = ThumbnailLayout.VisibleRows(0, 300, 100, 0);
            Assert.IsTrue(last < first);

            (first, last) = ThumbnailLayout.VisibleRows(0, 0, 100, 5);
            Assert.IsTrue(last < first);
        }

        // Grid used below (4 columns, 10 items):
        //  0  1  2  3
        //  4  5  6  7
        //  8  9
        [TestMethod]
        [DataRow(5, ThumbnailMove.Previous, 4)]
        [DataRow(0, ThumbnailMove.Previous, 0)]
        [DataRow(3, ThumbnailMove.Next, 4)]     // wraps to the next row
        [DataRow(9, ThumbnailMove.Next, 9)]
        [DataRow(6, ThumbnailMove.Up, 2)]
        [DataRow(2, ThumbnailMove.Up, 2)]       // top row stays
        [DataRow(1, ThumbnailMove.Down, 5)]
        [DataRow(5, ThumbnailMove.Down, 9)]
        [DataRow(6, ThumbnailMove.Down, 9)]     // nothing below, but the last row is lower -> last item
        [DataRow(9, ThumbnailMove.Down, 9)]     // already on the last row
        [DataRow(4, ThumbnailMove.First, 0)]
        [DataRow(4, ThumbnailMove.Last, 9)]
        public void Move_Follows_Grid(int index, ThumbnailMove move, int expected)
        {
            Assert.AreEqual(expected, ThumbnailLayout.Move(index, 10, 4, 1, move));
        }

        [TestMethod]
        public void PageDown_And_PageUp_Keep_The_Column()
        {
            // 4 columns, 30 items, 2 rows per page
            Assert.AreEqual(9, ThumbnailLayout.Move(1, 30, 4, 2, ThumbnailMove.PageDown));
            Assert.AreEqual(1, ThumbnailLayout.Move(9, 30, 4, 2, ThumbnailMove.PageUp));

            // near the top: goes to the first row, same column
            Assert.AreEqual(2, ThumbnailLayout.Move(6, 30, 4, 2, ThumbnailMove.PageUp));

            // near the bottom: last row is 28, 29 -> column 3 does not exist there, so the last item
            Assert.AreEqual(29, ThumbnailLayout.Move(23, 30, 4, 2, ThumbnailMove.PageDown));

            // already on the last row
            Assert.AreEqual(29, ThumbnailLayout.Move(29, 30, 4, 2, ThumbnailMove.PageDown));
        }

        [TestMethod]
        public void Move_Without_Selection_Or_Items()
        {
            Assert.AreEqual(0, ThumbnailLayout.Move(-1, 10, 4, 1, ThumbnailMove.Next));
            Assert.AreEqual(-1, ThumbnailLayout.Move(0, 0, 4, 1, ThumbnailMove.Next));
        }

        [TestMethod]
        public void Tile_Size_Includes_Chrome()
        {
            Assert.AreEqual(160 + ThumbnailLayout.ChromeWidth, ThumbnailLayout.TileWidth(160));
            Assert.AreEqual(160 + ThumbnailLayout.ChromeHeight, ThumbnailLayout.TileHeight(160));
            Assert.IsTrue(ThumbnailLayout.TileHeight(160) > ThumbnailLayout.TileWidth(160), "Height includes the file name");
        }
    }
}
