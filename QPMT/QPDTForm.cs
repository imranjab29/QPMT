using iText.Commons.Utils;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout.Element;
using Org.BouncyCastle.Bcpg.Sig;
using PdfiumViewer;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
//using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace QPMT
{
    public partial class QPDTForm : Form
    {
        private string loadedPdfPath;
        private PdfiumViewer.PdfDocument pdfDocument;

        private int currentPage = 0;
        private int totalPages = 0;

        private Point startPoint;
        private Point endPoint;
        private bool isDrawing = false;

        private bool isUndoingOrRedoing = false; //meeded to prevent textbox and cbbox from treating undo/redo actions

        private Bitmap pdfPageImage;
        private class AnnotationData
        {
            public int PageNumber { get; set; }
            public string Type { get; set; }
            public Point Start { get; set; }
            public Point End { get; set; }

            public string Text { get; set; } // ✅ Add this for text content
            public List<Point> PenPoints { get; set; } // ✅ for freehand
        }

        //declaring a structure we will save it on undo redo stacks for the marks undo redo functionality
        private struct UndoCommand
        {
            public string Type;              // "Marks"
            
            public AnnotationData Annotation;

            public int? DataGridRowIndex; // Only used for Marks
            
            // For TextBox and ComboBox
            public string ControlName;
            public string OldValue;
            public string NewValue;
        }

        private List<AnnotationData> actions = new List<AnnotationData>();

        private Stack<UndoCommand> undoStack = new Stack<UndoCommand>();
        private Stack<UndoCommand> redoStack = new Stack<UndoCommand>();

        private List<Point> currentPenPoints = new List<Point>();

        private float zoomFactor = 1.0f;

        private string selectedAnnotationType = "Select"; // Default tool

        //fields for panning using the Hand tool
        private bool isPanning = false;
        private Point panStartPoint = Point.Empty;
        private Point scrollPosition = Point.Empty; // If you use a Panel with AutoScroll
        private Point scrollStart;
        Point lastMousePosition;

        private bool pageSwitchedDuringPan = false; //flag to cooldown dragging after page switch

        // Tool Buttons
        //private Button btnLine, btnRectangle, btnCircle, btnBlur;

        //adding new font  support for right and wrong stamp.  the default helvitica font does not have right & wrong symbols.
        private static readonly PrivateFontCollection customFonts = new PrivateFontCollection();
        private static readonly FontFamily DejaVuSansFamily;


        //Marking text for pending annotations
        private string pendingMarkText = null;

        //def specific to examiner correction
        int AssignedPageNumber = 1; // zero-based index - hardocoded for now
        RectangleF AssignedRegion = new RectangleF(100, 500, 300, 150); // PDF coordinate space


        //def variables for Grid Marks Update
        int nCurQNumber = 0;
        float fCurQMarks = 0;

        public QPDTForm()
        {
            InitializeComponent();

            btnLine.Click += btnLine_Click;
            btnRectangle.Click += btnRectangle_Click;
            btnCircle.Click += btnCircle_Click;
            btnBlur.Click += btnBlur_Click;

            this.Controls.Add(btnLine);
            this.Controls.Add(btnRectangle);
            this.Controls.Add(btnCircle);
            this.Controls.Add(btnBlur);

            pictureBox1.MouseDown += PictureBox1_MouseDown;
            pictureBox1.MouseMove += PictureBox1_MouseMove;
            pictureBox1.MouseUp += PictureBox1_MouseUp;
            pictureBox1.MouseWheel += PictureBox1_MouseWheel;

            btnPrevPage.Click += btnPrevPage_Click;
            btnNextPage.Click += btnNextPage_Click;

            dataGridViewQuestions.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    ApplyAssignedRegionAndPageFromRow(dataGridViewQuestions.Rows[e.RowIndex]);
                }
            };

            //zoom controls
            txtZoom.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    if (int.TryParse(txtZoom.Text, out int val))
                    {
                        val = Math.Max(trackBarZoom.Minimum, Math.Min(trackBarZoom.Maximum, val));
                        trackBarZoom.Value = val;
                        zoomFactor = val / 100f;
                        RenderPage();
                    }
                }
            };

            SetControlsEnabled(false);

            typeof(Panel).InvokeMember("DoubleBuffered",
    System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
    null, panel1, new object[] { true });

        }

        static QPDTForm()
        {
            var customFonts = new PrivateFontCollection();
            customFonts.AddFontFile("DejaVuSans.ttf");
            DejaVuSansFamily = customFonts.Families[0];

        }
        private void btnLine_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Line";
            pictureBox1.Cursor = Cursors.Cross;

            HighlightSelectedTool(btnLine);
        }

        private void btnRectangle_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Rectangle";
            pictureBox1.Cursor = Cursors.Cross;

            HighlightSelectedTool(btnRectangle);
        }

        private void btnCircle_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Circle";
            pictureBox1.Cursor = Cursors.Cross;

            HighlightSelectedTool(btnCircle);
        }

        private void btnBlur_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Blur";
            pictureBox1.Cursor = Cursors.Cross;

            HighlightSelectedTool(btnBlur);
        }


        private void SelectHandTool()
        {
            selectedAnnotationType = "Select";
            pictureBox1.Cursor = Cursors.Hand;
            btnHand.BackColor = SystemColors.ActiveCaption;
            this.ActiveControl = btnHand;

            btnLine.BackColor = SystemColors.Control;
            btnRectangle.BackColor = SystemColors.Control;
            btnCircle.BackColor = SystemColors.Control;
            btnBlur.BackColor = SystemColors.Control;
            btnGotoPage.BackColor = SystemColors.Control;
            btnDemarcation1.BackColor = SystemColors.Control;
            btnDemarcation2.BackColor = SystemColors.Control;
        }

        private void HighlightSelectedTool(System.Windows.Forms.Button selectedButton)
        {
            // Reset all buttons to default
            btnHand.BackColor = SystemColors.Control;
            btnLine.BackColor = SystemColors.Control;
            btnRectangle.BackColor = SystemColors.Control;
            btnCircle.BackColor = SystemColors.Control;
            btnBlur.BackColor = SystemColors.Control;
            btnPen.BackColor = SystemColors.Control;
            btnText.BackColor = SystemColors.Control;
            btnWrongStamp.BackColor = SystemColors.Control;
            btnRightStamp.BackColor = SystemColors.Control;
            btnGotoPage.BackColor = SystemColors.Control;
            btnDemarcation1.BackColor = SystemColors.Control;
            btnDemarcation2.BackColor = SystemColors.Control;

            // Highlight the selected one
            selectedButton.BackColor = SystemColors.ActiveCaption;
            this.ActiveControl = selectedButton;
        }

        private void btnPrevPage_Click(object sender, EventArgs e)
        {
            if (pdfDocument == null) return;
            if (currentPage > 0) { currentPage--; RenderPage(); }
        }

        private void btnNextPage_Click(object sender, EventArgs e)
        {
            if (pdfDocument == null) return;
            if (currentPage < totalPages - 1) { currentPage++; RenderPage(); }
        }

        private void PictureBox1_MouseDown(object sender, MouseEventArgs e) //this fn checks assigned region check for all annotations
        {
            if (pdfPageImage == null) return;

            if (selectedAnnotationType == "Select" && e.Button == MouseButtons.Left)
            {
                isPanning = true;
                lastMousePosition = panel1.PointToScreen(e.Location);
                pageSwitchedDuringPan = false; // Reset each new pan
                return;
            }

            // ✅ Common click point (PictureBox coords)
            Point clickPoint = TransformMousePoint(e.Location);

            // ✅ Convert to PDF coords for AssignedRegion check
            using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(loadedPdfPath)))
            {
                PdfPage page = GetPdfPage(pdfDoc, currentPage);
                var pageSize = page.GetPageSize();

                float pdfWidth = pageSize.GetWidth();
                float pdfHeight = pageSize.GetHeight();

                float xRatio = pdfWidth / pictureBox1.Width;
                float yRatio = pdfHeight / pictureBox1.Height;

                float pdfX = clickPoint.X * xRatio;
                float pdfY = pdfHeight - (clickPoint.Y * yRatio);
            }

            isDrawing = true;

            if (selectedAnnotationType == "Pen")
            {
                currentPenPoints.Clear();
                currentPenPoints.Add(clickPoint);
            }
            else
            {
                // Any other tool (Rectangle, Line, etc.)
                startPoint = clickPoint;
            }
        }


        private PdfPage GetPdfPage(iText.Kernel.Pdf.PdfDocument doc, int zeroBasedPage)
        {
            //in itext pdf current page starts with 1 and not zero
            return doc.GetPage(zeroBasedPage + 1);
        }

        private void PictureBox1_MouseMove(object sender, MouseEventArgs e)
        {
            if (isPanning)
            {
                Point currentPosition = panel1.PointToScreen(e.Location);
                int dx = lastMousePosition.X - currentPosition.X;
                int dy = lastMousePosition.Y - currentPosition.Y;

                Point newPos = new Point(
                    panel1.HorizontalScroll.Value + dx,
                    panel1.VerticalScroll.Value + dy);

                // Clamp
                newPos.X = Math.Max(0, Math.Min(newPos.X, panel1.HorizontalScroll.Maximum));
                newPos.Y = Math.Max(0, Math.Min(newPos.Y, panel1.VerticalScroll.Maximum));

                panel1.AutoScrollPosition = new Point(newPos.X, newPos.Y);

                lastMousePosition = currentPosition;

                CheckPageScroll();
                return;
            }

            if (!isDrawing) return;

            if (selectedAnnotationType == "Select") return;

            if (selectedAnnotationType == "Pen")
            {
                currentPenPoints.Add(TransformMousePoint(e.Location));

                Bitmap tempImage = (Bitmap)pdfPageImage.Clone();
                foreach (var ann in actions)
                    if (ann.PageNumber == currentPage)
                        using (Graphics g = Graphics.FromImage(tempImage))
                            DrawAnnotation(g, ann);

                using (Graphics g = Graphics.FromImage(tempImage))
                    DrawPen(g, currentPenPoints);

                pictureBox1.Image = tempImage;
            }
            else
            {
                endPoint = TransformMousePoint(e.Location);

                Bitmap tempImage = (Bitmap)pdfPageImage.Clone();
                foreach (var ann in actions)
                    if (ann.PageNumber == currentPage)
                        using (Graphics g = Graphics.FromImage(tempImage))
                            DrawAnnotation(g, ann);

                using (Graphics g = Graphics.FromImage(tempImage))
                    DrawShape(g, startPoint, endPoint, selectedAnnotationType, new Pen(System.Drawing.Color.Red, 2));

                pictureBox1.Image = tempImage;
            }
        }

        private void PictureBox1_MouseUp(object sender, MouseEventArgs e)
        {
            if (isPanning && e.Button == MouseButtons.Left)
            {
                isPanning = false;
            }

            isDrawing = false;

            if (selectedAnnotationType == "Select")
            {
                return; // Do nothing for hand tool
            }

            if (selectedAnnotationType == "RightStamp")
            {
                var rightStamp = new AnnotationData
                {
                    Type = "Text",
                    Text = "✔️",
                    Start = e.Location,
                    End = e.Location,
                    PageNumber = currentPage
                };

                actions.Add(rightStamp);
                undoStack.Push(new UndoCommand
                {
                    Type = "RightStamp",
                    Annotation = rightStamp
                });
                redoStack.Clear();
            }
            else if (selectedAnnotationType == "WrongStamp")
            {
                var wrongStamp = new AnnotationData
                {
                    Type = "Text",
                    Text = "✖️",
                    Start = e.Location,
                    End = e.Location,
                    PageNumber = currentPage
                };
                actions.Add(wrongStamp);

                undoStack.Push(new UndoCommand
                {
                    Type = "WrongStamp",
                    Annotation = wrongStamp
                });

                redoStack.Clear();
            }
            else if (selectedAnnotationType == "Pen")
            {
                if (currentPenPoints.Count > 1)
                {
                    var penAnn = new AnnotationData
                    {
                        PageNumber = currentPage,
                        Type = "Pen",
                        PenPoints = new List<Point>(currentPenPoints)
                    };
                    actions.Add(penAnn);
                    undoStack.Push(new UndoCommand
                    {
                        Type = "Pen",
                        Annotation = penAnn
                    });
                    redoStack.Clear();
                }
            }
            else if (!string.IsNullOrEmpty(selectedAnnotationType)) // who is else now?
            {
                endPoint = TransformMousePoint(e.Location);

                if (selectedAnnotationType == "Demarcation1")
                {
                    //assign the rectangleF calculated to the textBoxDemarcation1
                    float x = Math.Min(startPoint.X, e.X);
                    float y = Math.Min(startPoint.Y, e.Y);
                    float width = Math.Abs(startPoint.X - e.X);
                    float height = Math.Abs(startPoint.Y - e.Y);

                    RectangleF rect = new RectangleF(x, y, width, height);
                    textBoxDemarcation1.Text = $"{rect.X}, {rect.Y}, {rect.Width}, {rect.Height}";
                    //assign page number to the textBoxPageumberDemarcation1
                    textBoxPage1.Text = (currentPage + 1).ToString(); // +1 because pages are 1-based in UI

                    /*
                    //PArse it back to the rectangleF
                    //Method 1 to parse the rectangle from the textBoxDemarcation1
                    if (RectangleF.TryParse(textBoxDemarcation1.Text, out RectangleF parsedRect))
                        {
                        ann.Start = new Point((int)parsedRect.X, (int)parsedRect.Y);
                        ann.End = new Point((int)(parsedRect.X + parsedRect.Width), (int)(parsedRect.Y + parsedRect.Height));
                    }
                    else
                    {
                        MessageBox.Show("Invalid rectangle format. Please use 'x, y, width, height'.");
                        return; // Cancel if parsing fails
                    }
                    //method 2 to parse the rectangle from the textBoxDemarcation1
                    string[] parts = textBoxDemarcation1.Text.Split(',');
                    if (parts.Length == 4 &&
                        float.TryParse(parts[0], out float x) &&
                        float.TryParse(parts[1], out float y) &&
                        float.TryParse(parts[2], out float width) &&
                        float.TryParse(parts[3], out float height))
                    {
                        RectangleF rect = new RectangleF(x, y, width, height);
                        // Use rect as needed
                    }*/
                }
                if (selectedAnnotationType == "Demarcation2")
                {
                    //assign the rectangleF calculated to the textBoxDemarcation1
                    float x = Math.Min(startPoint.X, e.X);
                    float y = Math.Min(startPoint.Y, e.Y);
                    float width = Math.Abs(startPoint.X - e.X);
                    float height = Math.Abs(startPoint.Y - e.Y);

                    RectangleF rect = new RectangleF(x, y, width, height);
                    textBoxDemarcation2.Text = $"{rect.X}, {rect.Y}, {rect.Width}, {rect.Height}";
                    //assign page number to the textBoxPageumberDemarcation1
                    textBoxPage2.Text = (currentPage + 1).ToString(); // +1 because pages are 1-based in UI

                    /*
                    //PArse it back to the rectangleF
                    //Method 1 to parse the rectangle from the textBoxDemarcation1
                    if (RectangleF.TryParse(textBoxDemarcation1.Text, out RectangleF parsedRect))
                        {
                        ann.Start = new Point((int)parsedRect.X, (int)parsedRect.Y);
                        ann.End = new Point((int)(parsedRect.X + parsedRect.Width), (int)(parsedRect.Y + parsedRect.Height));
                    }
                    else
                    {
                        MessageBox.Show("Invalid rectangle format. Please use 'x, y, width, height'.");
                        return; // Cancel if parsing fails
                    }
                    //method 2 to parse the rectangle from the textBoxDemarcation1
                    string[] parts = textBoxDemarcation1.Text.Split(',');
                    if (parts.Length == 4 &&
                        float.TryParse(parts[0], out float x) &&
                        float.TryParse(parts[1], out float y) &&
                        float.TryParse(parts[2], out float width) &&
                        float.TryParse(parts[3], out float height))
                    {
                        RectangleF rect = new RectangleF(x, y, width, height);
                        // Use rect as needed
                    }*/
                }


                var ann = new AnnotationData
                {
                    PageNumber = currentPage,
                    Type = selectedAnnotationType,
                    Start = startPoint,
                    End = endPoint
                };

                if (selectedAnnotationType == "Text")
                {
                    string input = Prompt.ShowDialog("Enter text:", "Text Annotation");
                    if (!string.IsNullOrEmpty(input))
                    {
                        ann.Text = input;
                    }
                    else
                    {
                        return; // Cancel if no text entered
                    }
                }
                if (selectedAnnotationType == "Demarcation1")
                    ann.Text = textBoxDemarcation1.Text;

                if (selectedAnnotationType == "Demarcation2")
                    ann.Text = textBoxDemarcation2.Text;


                actions.Add(ann);
                undoStack.Push(new UndoCommand
                {
                    Type = selectedAnnotationType,
                    Annotation = ann
                });
                redoStack.Clear();
            }

            UpdateUndoRedoButtons();
            RedrawCurrentPage();

            if (selectedAnnotationType == "Demarcation1" || selectedAnnotationType == "Demarcation2")
            {
                selectedAnnotationType = "Select";
                SelectHandTool();
            }
        }

        //functions to pann the pdf with mouse scroll plus go to the prev or next page
        private void CheckPageScroll()
        {
            if (pageSwitchedDuringPan) return; // only once per drag

            // The vertical offset is NEGATIVE when scrolled down
            int offsetY = panel1.AutoScrollPosition.Y;

            // 1️⃣ Scrolled up past the top?
            if (offsetY == 0)
            {
                if (currentPage > 0 && !pageSwitchedDuringPan)
                {
                    currentPage--;
                    RenderPage();
                    ScrollToBottom(); // When going to previous, land at bottom
                    pageSwitchedDuringPan = true;
                }
            }
            // 2️⃣ Scrolled down past the bottom?
            else if (Math.Abs(offsetY) + panel1.ClientSize.Height >= pictureBox1.Height)
            {
                if (currentPage < totalPages - 1 && !pageSwitchedDuringPan)
                {
                    currentPage++;
                    RenderPage();
                    ScrollToTop(); // When going forward, start at top
                    pageSwitchedDuringPan = true;
                }
            }
        }

        private void ScrollToTop()
        {
            panel1.AutoScrollPosition = new Point(0, 0);
        }

        private void ScrollToBottom()
        {
            int yMax = pictureBox1.Height - panel1.ClientSize.Height;
            if (yMax > 0)
                panel1.AutoScrollPosition = new Point(0, yMax);
        }

        private void CenterPageAtTop()
        {
            panel1.AutoScrollPosition = new Point(0, 0);
        }

        private void CenterPageAtBottom()
        {
            panel1.AutoScrollPosition = new Point(0, pictureBox1.Height);
        }

        //functions for free pen tool
        private void DrawPen(Graphics g, List<Point> points)
        {
            if (points.Count < 2) return;

            using (Pen pen = new Pen(System.Drawing.Color.Red, 2))
            {
                for (int i = 0; i < points.Count - 1; i++)
                {
                    g.DrawLine(pen, points[i], points[i + 1]);
                }
            }
        }


        private void btnUndo_Click(object sender, EventArgs e)
        {
            if (undoStack.Count > 0)
            {
                UndoCommand cmd = undoStack.Pop();
                redoStack.Push(cmd);
                isUndoingOrRedoing = true;

                //wait if the object was demarcation1 or demarcation2, we will need to reset the Area textbox and page number text box
                if (cmd.Type == "Demarcation1")
                {
                    textBoxDemarcation1.Text = string.Empty; // Reset the demarcation area textbox
                    textBoxPage1.Text = string.Empty; // Reset the page number textbox
                    actions.Remove(cmd.Annotation); //
                    //if the object is on a different page... lets move to that page
                    if (cmd.Annotation != null)
                    {
                        // If the annotation is on a different page, switch to it
                        if (currentPage != cmd.Annotation.PageNumber)
                        {
                            currentPage = cmd.Annotation.PageNumber;
                        }
                    }
                    RenderPage();
                }
                else if (cmd.Type == "Demarcation2")
                {
                    textBoxDemarcation2.Text = string.Empty; // Reset the demarcation area textbox
                    textBoxPage2.Text = string.Empty; // Reset the page number textbox
                    actions.Remove(cmd.Annotation); //
                    //if the object is on a different page... lets move to that page
                    if (cmd.Annotation != null)
                    {
                        // If the annotation is on a different page, switch to it
                        if (currentPage != cmd.Annotation.PageNumber)
                        {
                            currentPage = cmd.Annotation.PageNumber;
                        }
                    }
                    RenderPage();
                }
                else if (cmd.Type == "TextBox")
                {
                    var tb = this.Controls.Find(cmd.ControlName, true).FirstOrDefault() as TextBox;
                    if (tb != null)
                    {
                        tb.Text = cmd.OldValue;
                        tb.Focus();
                    }
                }
                else if (cmd.Type == "ComboBox")
                {
                    var cb = this.Controls.Find(cmd.ControlName, true).FirstOrDefault() as ComboBox;
                    if (cb != null)
                    {
                        cb.Text = cmd.OldValue;
                        cb.Focus();
                    }

                }
                else //for rest all of the annotations
                {
                    actions.Remove(cmd.Annotation); //
                    //if the object is on a different page... lets move to that page
                    if (cmd.Annotation != null)
                    {
                        // If the annotation is on a different page, switch to it
                        if (currentPage != cmd.Annotation.PageNumber)
                        {
                            currentPage = cmd.Annotation.PageNumber;
                        }
                    }
                    RenderPage();
                }


                //RedrawCurrentPage();
                isUndoingOrRedoing = false;
                UpdateUndoRedoButtons();
            }
        }

        private void btnRedo_Click(object sender, EventArgs e)
        {
            if (redoStack.Count > 0)
            {
                UndoCommand action = redoStack.Pop();
                undoStack.Push(action);
                isUndoingOrRedoing = true;
                //wait if the object was demarcation1 or demarcation2, we will need to reset the Area textbox and page number text box
                if (action.Type == "Demarcation1")
                {
                    textBoxDemarcation1.Text = action.Annotation.Text; // Reset the demarcation area textbox
                    textBoxPage1.Text = (action.Annotation.PageNumber + 1).ToString(); // Reset the page number textbox
                    actions.Add(action.Annotation);
                    //if the object is on a different page... lets move to that page
                    if (action.Annotation != null)
                    {
                        if (currentPage != action.Annotation.PageNumber)
                        {
                            currentPage = action.Annotation.PageNumber;

                        }
                    }
                    RenderPage();
                }
                else if (action.Type == "Demarcation2")
                {
                    textBoxDemarcation2.Text = action.Annotation.Text; // Reset the demarcation area textbox
                    textBoxPage2.Text = (action.Annotation.PageNumber + 1).ToString(); // Reset the page number textbox
                    actions.Add(action.Annotation);
                    //if the object is on a different page... lets move to that page
                    if (action.Annotation != null)
                    {
                        if (currentPage != action.Annotation.PageNumber)
                        {
                            currentPage = action.Annotation.PageNumber;

                        }
                    }
                    RenderPage();
                }
                else if (action.Type == "TextBox")
                { 
                   var tb = this.Controls.Find(action.ControlName, true).FirstOrDefault() as TextBox;
                    if (tb != null)
                    {
                        //set focus on the textbox
                        tb.Text = action.NewValue;
                        tb.Focus();
                    }
                }
                else if (action.Type == "ComboBox")
                {
                    var cb = this.Controls.Find(action.ControlName, true).FirstOrDefault() as ComboBox;
                    if (cb != null)
                    {
                        cb.Text = action.NewValue;
                        cb.Focus();
                    }
                }
                else
                {
                    actions.Add(action.Annotation);
                    //if the object is on a different page... lets move to that page
                    if (action.Annotation != null)
                    {
                        if (currentPage != action.Annotation.PageNumber)
                        {
                            currentPage = action.Annotation.PageNumber;

                        }
                    }
                    RenderPage();
                }
                isUndoingOrRedoing = false;
                UpdateUndoRedoButtons();
            }
        }
        
        private void RenderPage()
        {
            /* 
             * pdfPageImage = new Bitmap(pdfDocument.Render(currentPage, (int)(800 * zoomFactor), (int)(1000 * zoomFactor), true));
             * lblPageNumber.Text = $"Page {currentPage + 1} of {totalPages}";
             * RedrawCurrentPage(); 
             */
            if (pdfDocument == null) return;

            int renderWidth = (int)(800 * zoomFactor);
            int renderHeight = (int)(1000 * zoomFactor);

            pdfPageImage = new Bitmap(
                pdfDocument.Render(
                    currentPage,
                    renderWidth,
                    renderHeight,
                    96f * zoomFactor,
                    96f * zoomFactor,
                    PdfiumViewer.PdfRenderFlags.Annotations
                ));

            lblPageNumber.Text = $"Page {currentPage + 1} of {totalPages}";
            RedrawCurrentPage();
        }

        private void RedrawCurrentPage()
        {
            if (pdfPageImage == null) return;

            Bitmap tempImage = (Bitmap)pdfPageImage.Clone();

            using (Graphics g = Graphics.FromImage(tempImage))
            {
                // ✅ Draw normal annotations:
                foreach (var ann in actions)
                {
                    if (ann.PageNumber == currentPage)
                        DrawAnnotation(g, ann);
                }
            }


            /*        foreach (var ann in actions)
                            if (ann.PageNumber == currentPage)
                                using (Graphics g = Graphics.FromImage(tempImage))
                                    DrawAnnotation(g, ann);


                    // ✅ Draw the assigned region if it’s for this page:
                    DrawAssignedRegion(g); 
            */
            pictureBox1.Image = tempImage;

            pictureBox1.Width = tempImage.Width;   // ✅ Actual size
            pictureBox1.Height = tempImage.Height; // ✅ Actual size

        }

        private void DrawAnnotation(Graphics g, AnnotationData ann)
        {
            if (ann.Type == "Pen")
                DrawPen(g, ann.PenPoints);
            else if (ann.Type == "Blur") ApplyPixelBlurToGraphics(g, ann);
            else if (ann.Type == "Text")
            {
                DrawText(g, ann);
            }
            else if (ann.Type == "Text")
            {
                DrawText(g, ann);
            }
            else if (ann.Type == "Demarcation1" || ann.Type == "Demarcation2")
            {
                DrawText(g, ann); // Draw the demarcation text
                DrawShape(g, ann.Start, ann.End, ann.Type, new Pen(System.Drawing.Color.Red, 2));
            }
            else // Line, Rectangle, Circle
            {
                DrawShape(g, ann.Start, ann.End, ann.Type, new Pen(System.Drawing.Color.Red, 2));
            }
        }

        private void DrawText(Graphics g, AnnotationData ann)
        {
            bool isStamp = ann.Text == "✔️" || ann.Text == "✖️" ;

            float baseFontSize = isStamp ? 40f : 14f;
            float scaledFontSize = baseFontSize * zoomFactor;

            // 1️⃣ Logical position in your coordinate system

            float rawX, rawY;

            if (ann.Type == "Demarcation1" || ann.Type == "Demarcation2")
            {
                rawX = (ann.Start.X + ann.End.X)/2;
                rawY = (ann.Start.Y + ann.End.Y)/2;
            }
            else
            {
                rawX = ann.Start.X;
                rawY = ann.Start.Y;
            }

            using (Font font = isStamp 
                ? new Font(DejaVuSansFamily, scaledFontSize, GraphicsUnit.Pixel)
                : new Font("Helvetica", scaledFontSize, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(System.Drawing.Color.Red))
            {
                SizeF size = g.MeasureString(ann.Text, font);

                // 2️⃣ Scale logical point to screen pixels
                float drawX = rawX * zoomFactor;
                float drawY = rawY * zoomFactor;

                // 3️⃣ Fudge the pixel position to align text visually
                if (isStamp)
                {
                    // Center the text at click point
                    drawX -= size.Width / 2f;
                    drawY -= size.Height / 2f;
                }
                else
                {
                    // Normal text: align top-left, lift baseline
                    drawY -= size.Height;
                }

                if(ann.Type == "Demarcation1" || ann.Type == "Demarcation2")
                {
                    // 1. Draw the rectangle demarcation text // 2. Draw the page number of the question too
                    g.DrawString(ann.Text + " Page No:" + (ann.PageNumber+1).ToString(), font, new SolidBrush(System.Drawing.Color.Blue), new PointF(drawX, drawY)); // 4️ Draw the text at the calculated position
                }
                else
                    g.DrawString(ann.Text, font, brush, new PointF(drawX, drawY)); // 4️⃣ Draw the text at the calculated position

                // 5️⃣ If it’s a Mark, also draw a fixed-size circle around it
            }
        }

        private void ApplyPixelBlurToGraphics(Graphics g, AnnotationData blur)
        {
            Rectangle rect = GetRectangle(blur.Start, blur.End);

            if (rect.Width <= 0 || rect.Height <= 0)
                return; // Skip invalid blur

            using (Bitmap cropped = new Bitmap(rect.Width, rect.Height))
            {
                using (Graphics gCropped = Graphics.FromImage(cropped))
                {
                    gCropped.DrawImage(pdfPageImage, new Rectangle(0, 0, cropped.Width, cropped.Height), rect, GraphicsUnit.Pixel);
                }

                using (Bitmap blurred = BlurBitmap(cropped, 10))
                {
                    g.DrawImage(blurred, rect);
                }
            }
        }

        private Bitmap BlurBitmap(Bitmap img, int blurSize)
        {
            Bitmap b = new Bitmap(img.Width, img.Height); // ✅ Works in C# 7.3
            for (int xx = 0; xx < img.Width; xx++)
                for (int yy = 0; yy < img.Height; yy++)
                {
                    int avgR = 0, avgG = 0, avgB = 0, count = 0;
                    for (int x = xx; x < xx + blurSize && x < img.Width; x++)
                        for (int y = yy; y < yy + blurSize && y < img.Height; y++)
                        {
                            System.Drawing.Color p = img.GetPixel(x, y);
                            avgR += p.R; avgG += p.G; avgB += p.B; count++;
                        }
                    System.Drawing.Color avg = System.Drawing.Color.FromArgb(avgR / count, avgG / count, avgB / count);
                    for (int x = xx; x < xx + blurSize && x < img.Width; x++)
                        for (int y = yy; y < yy + blurSize && y < img.Height; y++)
                            b.SetPixel(x, y, avg);
                }
            return b;
        }

        private void DrawShape(Graphics g, Point s, Point e, string type, Pen pen)
         {
            // ✅ Apply zoom to start and end points
            Point sZoomed = new Point((int)(s.X * zoomFactor), (int)(s.Y * zoomFactor));
            Point eZoomed = new Point((int)(e.X * zoomFactor), (int)(e.Y * zoomFactor));

            if (type == "Line")
            {
                g.DrawLine(pen, sZoomed, eZoomed);
            }
            else if (type == "Rectangle" || type == "Blur")
            {
                g.DrawRectangle(pen, GetRectangle(sZoomed, eZoomed));
            }
            else if (type == "Demarcation1" || type == "Demarcation2")
            {
                using (Pen pen1 = new Pen(System.Drawing.Color.Blue, 2))
                {
                    pen1.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    g.DrawRectangle(pen1, GetRectangle(sZoomed, eZoomed));
                }
            }
            else if (type == "Circle")
            {
                g.DrawEllipse(pen, GetRectangle(sZoomed, eZoomed));
            }
        }

        private Rectangle GetRectangle(Point p1, Point p2) =>
            new Rectangle(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Abs(p1.X - p2.X), Math.Abs(p1.Y - p2.Y));

        private void PictureBox1_MouseWheel(object sender, MouseEventArgs e)
        {
            if (pdfDocument == null) return;

            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                // Zoom with Ctrl + Scroll
                int step = e.Delta > 0 ? 5 : -5; // or 10 for bigger steps
                int newZoom = trackBarZoom.Value + step;

                // Clamp to min/max
                newZoom = Math.Max(trackBarZoom.Minimum, Math.Min(trackBarZoom.Maximum, newZoom));

                trackBarZoom.Value = newZoom; // This will trigger the Scroll event automatically

                // Manually update textbox too
                txtZoom.Text = newZoom.ToString();

                // Update zoom factor & render
                zoomFactor = newZoom / 100f;
                RenderPage();
            }
            else
            {
                // Normal scroll = page navigation
                if (e.Delta > 0 && currentPage > 0)
                    currentPage--;
                else if (e.Delta < 0 && currentPage < totalPages - 1)
                    currentPage++;

                RenderPage();
            }
        }
        public static class Prompt
        {
            public static string ShowDialog(string text, string caption)
            {
                Form prompt = new Form()
                {
                    Width = 400,
                    Height = 150,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    Text = caption,
                    StartPosition = FormStartPosition.CenterScreen
                };
                Label textLabel = new Label() { Left = 20, Top = 20, Text = text, Width = 350 };
                TextBox textBox = new TextBox() { Left = 20, Top = 50, Width = 350 };
                Button confirmation = new Button() { Text = "OK", Left = 280, Width = 90, Top = 80, DialogResult = DialogResult.OK };
                prompt.Controls.Add(textBox);
                prompt.Controls.Add(confirmation);
                prompt.Controls.Add(textLabel);
                prompt.AcceptButton = confirmation;

                return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.FormClosing += Form1_FormClosing;

            foreach (Control ctrl in this.Controls)
            {
                if (ctrl is Label)
                    ctrl.TabStop = false;
            }

            AttachUndoHandlers(this);
            UpdateUndoRedoButtons();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!string.IsNullOrEmpty(loadedPdfPath) && actions.Count > 0)
            {
                var result = MessageBox.Show(
                    "You have unsaved annotations. Do you want to save the PDF before closing?",
                    "Unsaved Changes",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    saveToolStripMenuItem_Click(sender, e);
                    //call exit here

                }
                else if (result == DialogResult.Cancel)
                {
                    e.Cancel = true; // Stop closing!
                }
                // else No => continue closing
            }
        }

 
        // Example: transform mouse coordinates to match pdfPageImage size
        private System.Drawing.Point TransformMousePoint(System.Drawing.Point mousePoint)
        {
            if (pictureBox1.Image == null) return mousePoint;

            float imgW = pdfPageImage.Width;
            float imgH = pdfPageImage.Height;
            float boxW = pictureBox1.Width;
            float boxH = pictureBox1.Height;

            float scaleX = imgW / boxW;
            float scaleY = imgH / boxH;

            int newX = (int)(mousePoint.X * scaleX);
            int newY = (int)(mousePoint.Y * scaleY);

            return new System.Drawing.Point(newX, newY);
        }
        
        private void SetControlsEnabled(bool enabled)
        {
            foreach (Control ctrl in this.Controls)
                    ctrl.Enabled = enabled;

            //enable file menu items always
            fileToolStripMenuItem.GetCurrentParent().Enabled = true;
            fileToolStripMenuItem.Enabled = true;
            saveToolStripMenuItem.Enabled = true;
            saveAsToolStripMenuItem.Enabled = true;
            exitToolStripMenuItem.Enabled = true;

        }
        private void SetControlsDefault()
        {
            this.textBoxExamName.Text = "Final Examination Aug 2025"; // Set default exam name
            this.textBoxCourse.Text = "First Year BA"; // Set default course name    
            this.textBoxPaperName.Text = "General Studies & General Knowledge"; // Set default paper name

            //textBoxExamName, Course & textBoxPaperName should be drop down in future. Details to be added by Super Admin

            this.textBoxPaperCode.Text = "P-1"; // Set default paper code
            this.dateTimeExam.Text = DateTime.Now.ToString("01/08/2025"); // Set default date

            this.textBoxTotQuestions.Text = "48"; // Set default total questions
            this.textBoxTotPages.Text = "32"; // Set default total pages

            this.textBoxTotMarks.Text = "200"; // Set default total marks    
            this.textBoxPassMarks.Text = "35"; // Set default pass marks

            this.textBoxLangMed.Text = "English"; // Set default language medium
            this.textBox2ndLang.Text="Hindi"; // Set default second language

            cbQSection.SelectedIndex = 0;
            cbQUnit.SelectedIndex = 0;
            cbQPart.SelectedIndex = 1;
            
            cbQType.SelectedIndex = 0; // Set default question type
            cbQNumber.SelectedIndex = 0; // Set default question mark
            cbQMaxMarks.SelectedIndex = 9; // Set default max marks
        }


        private void UpdateUndoRedoButtons()
        {
            btnUndo.Enabled = undoStack.Count > 0;
            btnRedo.Enabled = redoStack.Count > 0;
        }
      
        private void trackBarZoom_Scroll(object sender, EventArgs e)
        {
            int newVal = trackBarZoom.Value;
            txtZoom.Text = newVal.ToString();
            zoomFactor = newVal / 100f;

            UpdateAnnotationButtons();
            RenderPage();
        }
        
        private void txtZoom_TextChanged(object sender, EventArgs e)
        {
                if (int.TryParse(txtZoom.Text, out int val))
                {
                    val = Math.Max(trackBarZoom.Minimum, Math.Min(trackBarZoom.Maximum, val));

                    trackBarZoom.Value = val;
                    zoomFactor = val / 100f;

                    UpdateAnnotationButtons();

                    RenderPage();
                }
        }
        
        private void txtZoom_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (int.TryParse(txtZoom.Text, out int val))
                {
                    val = Math.Max(trackBarZoom.Minimum, Math.Min(trackBarZoom.Maximum, val));

                    trackBarZoom.Value = val;
                    zoomFactor = val / 100f;
                    
                    UpdateAnnotationButtons();
                    
                    RenderPage();
                }
            }
        }
       
        private void btnText_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Text";
            pictureBox1.Cursor = Cursors.Hand;

            HighlightSelectedTool(btnText);
        }
        
        private void btnHand_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Select";
            pictureBox1.Cursor = Cursors.Hand;
            HighlightSelectedTool(btnHand);
        }

        private void btnPen_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "Pen";
            pictureBox1.Cursor = Cursors.Cross;
            HighlightSelectedTool(btnPen);
        }
        private void btnRightStamp_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "RightStamp";
            pictureBox1.Cursor = Cursors.Cross;
            HighlightSelectedTool(btnRightStamp);
        }

        private void btnWrongStamp_Click(object sender, EventArgs e)
        {
            selectedAnnotationType = "WrongStamp";
            pictureBox1.Cursor = Cursors.Cross;
            HighlightSelectedTool(btnWrongStamp);
        }

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Z)
            {
                Undo(); // your existing undo logic
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.R)
            {
                Redo(); // your existing redo logic
                e.SuppressKeyPress = true;
            }
        }
        private void Undo()
        {
            btnUndo.PerformClick();
        }

        private void Redo()
        {
            btnRedo.PerformClick();
        }

        private void UpdateAnnotationButtons()
        {
            bool enable = Math.Abs(zoomFactor - 1.0f) < 0.001f;

            btnPen.Enabled = enable;
            btnText.Enabled = enable;
            btnRectangle.Enabled = enable;
            btnBlur.Enabled = enable;
            btnLine.Enabled = enable;
            btnCircle.Enabled = enable;
            btnRightStamp.Enabled = enable;
            btnWrongStamp.Enabled = enable;
        }

        private void label3_Click(object sender, EventArgs e)
        {

        }

        private void btnZoom100_Click(object sender, EventArgs e)
        {
            txtZoom.Text = "100";
            trackBarZoom.Value = 100;
            zoomFactor = 1.0f;

            UpdateAnnotationButtons();
            RenderPage();
        }

        private void btnGotoPage_Click(object sender, EventArgs e)
        {
            if (int.TryParse(txtGotoPage.Text.Trim(), out int pageNum))
            {
                // Pages shown to user are usually 1-based, so adjust to 0-based index
                if (pageNum >= 1 && pageNum <= totalPages)
                {
                    currentPage = pageNum - 1;
                    RenderPage();
                }
                else
                {
                    MessageBox.Show($"Please enter a page number between 1 and {totalPages}.");
                }
            }
            else
            {
                MessageBox.Show("Please enter a valid number.");
            }
        }

        private void txtGotoPage_TextChanged(object sender, EventArgs e)
        {

        }
        private void txtGotoPage_Enter(object sender, EventArgs e)
        {
            txtGotoPage.SelectAll();
        }

        private void txtGotoPage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                btnGotoPage.PerformClick();
            }
        }

        private void toolStripMenuItem2_Click(object sender, EventArgs e)
        {

        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // 🔑 CHECK for unsaved edits first
            if (!string.IsNullOrEmpty(loadedPdfPath) && actions.Count > 0)
            {
                var result = MessageBox.Show(
                    "You have unsaved annotations. Do you want to save the current PDF before loading a new one?",
                    "Unsaved Changes",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    // call save menu here
                    // Simply call your Save method!
                    //saveToolStripMenuItemItem_Click(sender, e);
                }
                else if (result == DialogResult.Cancel)
                {
                    return; // 🔑 Do not continue if user cancels
                }
            }

            using (var ofd = new OpenFileDialog())
            {
                ofd.Filter = "PDF files (*.pdf)|*.pdf";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    pdfDocument?.Dispose();
                    pdfPageImage?.Dispose();

                    loadedPdfPath = ofd.FileName;

                    try
                    {
                        pdfDocument = PdfiumViewer.PdfDocument.Load(loadedPdfPath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(
                            $"Failed to load PDF. The file may be corrupted or unreadable.\n\nError: {ex.Message}",
                            "Error Loading PDF",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                        pdfDocument = null;
                        loadedPdfPath = null;
                        SetControlsEnabled(false);
                        this.Text = "QPMT";
                        return;
                    }

                    totalPages = pdfDocument.PageCount;
                    currentPage = 0;

                    actions.Clear();

                    undoStack.Clear();
                    redoStack.Clear();

                    RenderPage();
                    this.Text = $"QPMT - {Path.GetFileName(loadedPdfPath)}";
                    SetControlsEnabled(true); // ✅ Enable after load

                    //This will set default values to some controls for demo purpos
                    SetControlsDefault(); // ✅ Enable after load
                    UpdateUndoRedoButtons();
                    SelectHandTool();
                }
            }

        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(loadedPdfPath)) return;


            try
            {
                // ✅ Fully release locks
                if (pdfDocument != null)
                {
                    pdfDocument.Dispose();
                    pdfDocument = null;
                }

                if (pdfPageImage != null)
                {
                    pdfPageImage.Dispose();
                    pdfPageImage = null;
                }

                // BEFORE saving, remember page:
                int pageToRestore = currentPage;

                // ✅ Force a GC to ensure file handle is released
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // ✅ Temp path for safe save
                string tempPath = Path.Combine(
                    Path.GetDirectoryName(loadedPdfPath),
                    Path.GetFileNameWithoutExtension(loadedPdfPath) + "_temp.pdf"
                );

                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(
                    new iText.Kernel.Pdf.PdfReader(loadedPdfPath),
                    new iText.Kernel.Pdf.PdfWriter(tempPath)))
                {
                    foreach (var ann in actions)
                    {
                        if (ann.Type == "Blur") continue;

                        PdfPage page = GetPdfPage(pdfDoc, ann.PageNumber);


                        var pageSize = page.GetPageSize();
                        float pdfWidth = pageSize.GetWidth();
                        float pdfHeight = pageSize.GetHeight();
                        float xRatio = pdfWidth / pictureBox1.Width;
                        float yRatio = pdfHeight / pictureBox1.Height;

                        float x1 = ann.Start.X * xRatio;
                        float y1 = pdfHeight - (ann.Start.Y * yRatio);
                        float x2 = ann.End.X * xRatio;
                        float y2 = pdfHeight - (ann.End.Y * yRatio);

                        var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page);

                        switch (ann.Type)
                        {
                            case "Pen":
                                canvas.SetStrokeColor(ColorConstants.RED).SetLineWidth(1);

                                var pdfPoints = ann.PenPoints
                                    .Select(p => new iText.Kernel.Geom.Point(p.X * xRatio, pdfHeight - p.Y * yRatio))
                                    .ToList();

                                if (pdfPoints.Count > 1)
                                {
                                    canvas.MoveTo(pdfPoints[0].GetX(), pdfPoints[0].GetY());
                                    for (int i = 1; i < pdfPoints.Count; i++)
                                    {
                                        canvas.LineTo(pdfPoints[i].GetX(), pdfPoints[i].GetY());
                                    }
                                    canvas.Stroke();
                                }
                                break;

                            case "Line":
                                canvas.SetStrokeColor(iText.Kernel.Colors.ColorConstants.RED)
                                      .SetLineWidth(1)
                                      .MoveTo(x1, y1)
                                      .LineTo(x2, y2)
                                      .Stroke();
                                break;

                            case "Rectangle":
                                float rectX = Math.Min(x1, x2);
                                float rectY = Math.Min(y1, y2);
                                float rectW = Math.Abs(x1 - x2);
                                float rectH = Math.Abs(y1 - y2);
                                canvas.SetStrokeColor(iText.Kernel.Colors.ColorConstants.RED)
                                      .SetLineWidth(1)
                                      .Rectangle(rectX, rectY, rectW, rectH)
                                      .Stroke();
                                break;

                            case "Circle":
                                float cx = (x1 + x2) / 2;
                                float cy = (y1 + y2) / 2;
                                float radiusX = Math.Abs(x2 - x1) / 2;
                                float radiusY = Math.Abs(y2 - y1) / 2;
                                canvas.SetStrokeColor(iText.Kernel.Colors.ColorConstants.RED)
                                      .SetLineWidth(1)
                                      .Ellipse(cx - radiusX, cy - radiusY, cx + radiusX, cy + radiusY)
                                      .Stroke();
                                break;

                            /*                            case "Text": //only text
                                                            {
                                                                var pdfFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
                                                                float fontSize = 12;

                                                                float tx = ann.Start.X * xRatio;
                                                                float ty = pdfHeight - (ann.Start.Y * yRatio);

                                                                // Adjust for text baseline so it matches PictureBox click.
                                                                ty -= fontSize; // empirical fudge to align better

                                                                canvas.BeginText()
                                                                      .SetFontAndSize(pdfFont, fontSize)
                                                                      .SetFillColor(iText.Kernel.Colors.ColorConstants.RED)
                                                                      .MoveText(tx, ty)
                                                                      .ShowText(ann.Text)
                                                                      .EndText();
                                                                break;
                                                            }
                            */
                            case "Text":
                                {
                                    string text = ann.Text?.Trim();
                                    float fontSize = 12;

                                    bool isStamp = text == "✔️" || text == "✖️";

                                    PdfFont pdfFont;
                                    if (isStamp)
                                    {
                                        fontSize = 42;

                                        string ttfPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DejaVuSans.ttf");
                                        pdfFont = PdfFontFactory.CreateFont(ttfPath, PdfEncodings.IDENTITY_H);




                                        char ch = text[0];
                                        var glyph = pdfFont.GetFontProgram().GetGlyph(ch);
                                        if (glyph == null)
                                            throw new Exception($"Glyph for '{ch}' not found in font program.");

                                        // Get bounding box
                                        float xMin = glyph.GetBbox()[0];
                                        float xMax = glyph.GetBbox()[2];
                                        float yMin = glyph.GetBbox()[1];
                                        float yMax = glyph.GetBbox()[3];

                                        float bboxWidth = (xMax - xMin) / 1000f * fontSize;
                                        float bboxHeight = (yMax - yMin) / 1000f * fontSize;

                                        // Optional fudge factor (5% of width)
                                        float fudge = bboxWidth * 0.05f;

                                        // 🗝 Correct by the left side bearing!
                                        float tx = ann.Start.X * xRatio - bboxWidth / 2 - (xMin / 1000f * fontSize) / 2 - fudge;
                                        float ty = pdfHeight - (ann.Start.Y * yRatio) - bboxHeight / 2 + fudge;

                                        canvas.BeginText()
                                              .SetFontAndSize(pdfFont, fontSize)
                                              .SetFillColor(ColorConstants.RED)
                                              .MoveText(tx, ty)
                                              .ShowText(text)
                                              .EndText();
                                    }
                                    else
                                    {
                                        pdfFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

                                        float tx = ann.Start.X * xRatio;

                                        // 2️⃣ Match the DrawText pixel size logic:
                                        float baseFontSizePx = 15f; // same as your DrawText base for normal text
                                        float fontSizePt = baseFontSizePx * 72f / 96f;

                                        // Baseline correction: approximate ascent fudge for Helvetica ~80%
                                        float ty = pdfHeight - (ann.Start.Y * yRatio) + (fontSizePt * 0.2f);

                                        canvas.BeginText()
                                              .SetFontAndSize(pdfFont, fontSizePt)
                                              .SetFillColor(ColorConstants.RED)
                                              .MoveText(tx, ty)
                                              .ShowText(text)
                                              .EndText();
                                    }
                                    break;
                                }
                        }
                    }
                }

                // ✅ Ensure GC again to release any unmanaged handles
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // ✅ Replace original with temp
                try
                {
                    if (File.Exists(loadedPdfPath))
                    {
                        File.Delete(loadedPdfPath);
                    }
                    File.Move(tempPath, loadedPdfPath);
                }
                catch (IOException ioEx)
                {
                    MessageBox.Show("Error replacing PDF: " + ioEx.Message);
                    return;
                }

                MessageBox.Show("PDF saved successfully!");

                // ✅ Reload
                pdfDocument = PdfiumViewer.PdfDocument.Load(loadedPdfPath);
                totalPages = pdfDocument.PageCount;

                // Restore current page to give the feeling of being on same page while saving:
                currentPage = Math.Max(0, Math.Min(pageToRestore, totalPages - 1));

                actions.Clear();

                undoStack.Clear();
                redoStack.Clear();
                UpdateUndoRedoButtons();

                RenderPage();

                this.Text = $"QPMT - {Path.GetFileName(loadedPdfPath)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error while saving PDF: " + ex.Message);
            }

        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(loadedPdfPath) && actions.Count > 0)
            {
                var result = MessageBox.Show(
                    "Do you want me to save the PDF before closing?",
                    "Exit",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    // ✅ Just call the same logic as your Save button
                    // call save menu here
                    saveToolStripMenuItem_Click(sender, e);
                    Application.Exit();
                }
                else if (result == DialogResult.No)
                {
                    Application.Exit();
                }
                // Cancel does nothing
            }
            else
            {
                Application.Exit();
            }

        }

        private void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // 🔑 Temporarily disabled this button to avoid confusion
            MessageBox.Show("This button is temporarily disabled.");
            return; // 🔑 Disabled for now, use btnSavePdf_Click instead

            /*if (string.IsNullOrEmpty(loadedPdfPath)) return;

            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "PDF files (*.pdf)|*.pdf";
            sfd.FileName = "Annotated_" + System.IO.Path.GetFileName(loadedPdfPath);

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                if (pdfDocument != null)
                {
                    pdfDocument.Dispose();
                    pdfDocument = null;
                }

                int imgWidth = pdfPageImage?.Width ?? 1;   // ✅ fallback to 1 to avoid div by zero
                int imgHeight = pdfPageImage?.Height ?? 1; // ✅

                pdfPageImage?.Dispose();
                pdfPageImage = null;

                string tempPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(sfd.FileName),
                    System.IO.Path.GetFileNameWithoutExtension(sfd.FileName) + "_temp.pdf");

                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(
                    new iText.Kernel.Pdf.PdfReader(loadedPdfPath),
                    new iText.Kernel.Pdf.PdfWriter(tempPath)))
                {
                    foreach (var ann in actions)
                    {
                        if (ann.Type == "Blur") continue;

                        PdfPage page = pdfDoc.GetPage(ann.PageNumber + 1);
                        var pageSize = page.GetPageSize();
                        float pdfWidth = pageSize.GetWidth();
                        float pdfHeight = pageSize.GetHeight();

                        //float xRatio = pdfWidth / pictureBox1.Width;
                        //float yRatio = pdfHeight / pictureBox1.Height;

                        float xRatio = pdfWidth / imgWidth; //drawing on picturebox and on actual pdf aligned.
                        float yRatio = pdfHeight / imgHeight; //drawing on picturebox and on actual pdf aligned.

                        float x1 = ann.Start.X * xRatio;
                        float y1 = pdfHeight - (ann.Start.Y * yRatio);
                        float x2 = ann.End.X * xRatio;
                        float y2 = pdfHeight - (ann.End.Y * yRatio);

                        var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page);

                        switch (ann.Type)
                        {
                            case "Pen":
                                canvas.SetStrokeColor(ColorConstants.RED).SetLineWidth(1);

                                var pdfPoints = ann.PenPoints
                                    .Select(p => new iText.Kernel.Geom.Point(p.X * xRatio, pdfHeight - p.Y * yRatio))
                                    .ToList();

                                if (pdfPoints.Count > 1)
                                {
                                    canvas.MoveTo(pdfPoints[0].GetX(), pdfPoints[0].GetY());
                                    for (int i = 1; i < pdfPoints.Count; i++)
                                    {
                                        canvas.LineTo(pdfPoints[i].GetX(), pdfPoints[i].GetY());
                                    }
                                    canvas.Stroke();
                                }
                                break;
                            case "Line":
                                canvas.SetStrokeColor(iText.Kernel.Colors.ColorConstants.RED)
                                      .SetLineWidth(1)
                                      .MoveTo(x1, y1)
                                      .LineTo(x2, y2)
                                      .Stroke();
                                break;

                            case "Rectangle":
                                float rectX = Math.Min(x1, x2);
                                float rectY = Math.Min(y1, y2);
                                float rectW = Math.Abs(x1 - x2);
                                float rectH = Math.Abs(y1 - y2);
                                canvas.SetStrokeColor(iText.Kernel.Colors.ColorConstants.RED)
                                      .SetLineWidth(1)
                                      .Rectangle(rectX, rectY, rectW, rectH)
                                      .Stroke();
                                break;

                            case "Circle":
                                float cx = (x1 + x2) / 2;
                                float cy = (y1 + y2) / 2;
                                float radiusX = Math.Abs(x2 - x1) / 2;
                                float radiusY = Math.Abs(y2 - y1) / 2;
                                canvas.SetStrokeColor(iText.Kernel.Colors.ColorConstants.RED)
                                      .SetLineWidth(1)
                                      .Ellipse(cx - radiusX, cy - radiusY, cx + radiusX, cy + radiusY)
                                      .Stroke();
                                break;

                            case "Text":
                                {
                                    var pdfFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
                                    float fontSize = 12;

                                    float tx = ann.Start.X * xRatio;
                                    float ty = pdfHeight - (ann.Start.Y * yRatio);

                                    // Adjust for text baseline so it matches PictureBox click.
                                    ty -= fontSize; // empirical fudge to align better

                                    canvas.BeginText()
                                          .SetFontAndSize(pdfFont, fontSize)
                                          .SetFillColor(iText.Kernel.Colors.ColorConstants.RED)
                                          .MoveText(tx, ty)
                                          .ShowText(ann.Text)
                                          .EndText();
                                    break;
                                }
                        }
                    }
                }

                // ✅ If the target exists, replace — else just move
                if (File.Exists(sfd.FileName))
                {
                    File.Replace(tempPath, sfd.FileName, null);
                }
                else
                {
                    File.Move(tempPath, sfd.FileName);
                }

                MessageBox.Show("Annotated PDF saved!");

                // Load saved PDF
                loadedPdfPath = sfd.FileName;

                try
                {
                    pdfDocument = PdfiumViewer.PdfDocument.Load(loadedPdfPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to load PDF. The file may be corrupted or unreadable.\n\nError: {ex.Message}",
                        "Error Loading PDF",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    pdfDocument = null;
                    loadedPdfPath = null;
                    SetControlsEnabled(false);
                    this.Text = "QPMT";
                    return;
                }

                totalPages = pdfDocument.PageCount;
                currentPage = 0;

                actions.Clear();

                undoStack.Clear();
                redoStack.Clear();
                UpdateUndoRedoButtons();

                RenderPage();

                this.Text = $"QPMT - {System.IO.Path.GetFileName(loadedPdfPath)}";
            }*/

        }

        private void menuStrip1_ItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {

        }

        private void btnDemarcation1_Click(object sender, EventArgs e)
        {
            //if the demarcation1 textbox has a value already, check if the old one needs to be deleted?
            if (!string.IsNullOrWhiteSpace(textBoxDemarcation1.Text))
            {
                var result = MessageBox.Show(
                    "You had already chosen a demarcation. Do you want re mark the demarcation again?",
                    "Demarcation Exists",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    //re do the demarcation. The old demarcation from the action and undo redo list needs to be deleted 
                    //delete the old demarcation from the actions list
                    
                    int count = 0;

                    if (int.TryParse(textBoxPage1.Text, out int pageNumber))
                    {
                        count = actions.RemoveAll(a =>
                            a.Type == "Demarcation1" &&
                            a.PageNumber == pageNumber - 1 && //ann contains 0-based index
                            a.Text == textBoxDemarcation1.Text);
                    }
                    else
                    {
                        // Handle invalid input if needed
                        MessageBox.Show("Invalid page number.");
                    }

                    if (count > 0)
                    {
                        //if we removed any demarcation, we need to update the undo and redo stacks
                        //re do the demarcation. The old demarcation from the action and undo redo list needs to be deleted

                        //delete the old demarcation from the undo list if its in it
                        if (int.TryParse(textBoxPage1.Text, out int pageNum))
                        {
                                                        string targetText = textBoxDemarcation1.Text;
                                                        Stack<UndoCommand> tempStack = new Stack<UndoCommand>();

                                                        // Pop all commands, skipping the ones we want to remove
                                                        while (undoStack.Count > 0)
                                                        {
                                                            var cmd = undoStack.Pop();

                                                            bool isTarget = cmd.Type == "Demarcation1"
                                                                && cmd.Annotation.PageNumber == pageNum - 1
                                                                && cmd.Annotation.Text == targetText;

                                                            if (!isTarget)
                                                                tempStack.Push(cmd); // keep
                                                        }

                                                        // Rebuild original stack in correct order
                                                        while (tempStack.Count > 0)
                                                        {
                                                            undoStack.Push(tempStack.Pop());
                                                        }
                                                    }

                                                    //delete the old demarcation from the redo list if its in it
                                                    if (int.TryParse(textBoxPage1.Text, out int pageNum2))
                                                    {
                                                        string targetText = textBoxDemarcation1.Text;

                                                        Stack<UndoCommand> tempStack = new Stack<UndoCommand>();

                                                        // Pop all commands, skipping the ones we want to remove
                                                        while (redoStack.Count > 0)
                                                        {
                                                            var cmd = redoStack.Pop();

                                                            bool isTarget = cmd.Type == "Demarcation1"
                                                                && cmd.Annotation.PageNumber == pageNum2 - 1
                                                                && cmd.Annotation.Text == targetText;

                                                            if (!isTarget)
                                                                tempStack.Push(cmd); // keep
                                                        }

                                                        // Rebuild original stack in correct order
                                                        while (tempStack.Count > 0)
                                                        {
                                                            redoStack.Push(tempStack.Pop());
                                                        }
                                                    }
                            
                        UpdateUndoRedoButtons();
                        RenderPage(); //re render the page to reflect the changes
                        MessageBox.Show("Old demarcation removed successfully.");
                        //reset the demarcation textbox & page textbox
                        textBoxDemarcation1.Text = string.Empty;
                        textBoxPage1.Text = string.Empty;
                    }
                    else
                    {
                        MessageBox.Show("No existing demarcation found to remove.");
                    }

                }
                else //if (result == DialogResult.Cancel)
                {
                    selectedAnnotationType = "Select";
                    SelectHandTool();
                    return; //looks like the user pressed cancel and wants to retain old values.
                }
            }

            selectedAnnotationType = "Demarcation1";
            pictureBox1.Cursor = Cursors.Cross;

            HighlightSelectedTool(btnDemarcation1);
        }

        private void btnDemarcation2_Click(object sender, EventArgs e)
        {
            //if the demarcation1 textbox has a value already, check if the old one needs to be deleted?
            if (!string.IsNullOrWhiteSpace(textBoxDemarcation2.Text))
            {
                var result = MessageBox.Show(
                    "You had already chosen a demarcation. Do you want re mark the demarcation again?",
                    "Demarcation Exists",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    //re do the demarcation. The old demarcation from the action and undo redo list needs to be deleted 
                    //delete the old demarcation from the actions list
                    //re do the demarcation. The old demarcation from the action and undo redo list needs to be deleted 
                    //delete the old demarcation from the actions list

                    int count = 0;

                    if (int.TryParse(textBoxPage2.Text, out int pageNumber))
                    {
                        count = actions.RemoveAll(a =>
                            a.Type == "Demarcation2" &&
                            a.PageNumber == pageNumber - 1 && //ann contains 0-based index
                            a.Text == textBoxDemarcation2.Text);
                    }
                    else
                    {
                        // Handle invalid input if needed
                        MessageBox.Show("Invalid page number.");
                    }

                    if (count > 0)
                    {
                        //if we removed any demarcation, we need to update the undo and redo stacks
                        //if we removed any demarcation, we need to update the undo and redo stacks
                        //re do the demarcation. The old demarcation from the action and undo redo list needs to be deleted

                        //delete the old demarcation from the undo list if its in it
                        if (int.TryParse(textBoxPage2.Text, out int pageNum))
                        {
                            string targetText = textBoxDemarcation2.Text;
                            Stack<UndoCommand> tempStack = new Stack<UndoCommand>();

                            // Pop all commands, skipping the ones we want to remove
                            while (undoStack.Count > 0)
                            {
                                var cmd = undoStack.Pop();

                                bool isTarget = cmd.Type == "Demarcation2"
                                    && cmd.Annotation.PageNumber == pageNum - 1
                                    && cmd.Annotation.Text == targetText;

                                if (!isTarget)
                                    tempStack.Push(cmd); // keep
                            }

                            // Rebuild original stack in correct order
                            while (tempStack.Count > 0)
                            {
                                undoStack.Push(tempStack.Pop());
                            }
                        }

                        //delete the old demarcation from the redo list if its in it
                        if (int.TryParse(textBoxPage2.Text, out int pageNum2))
                        {
                            string targetText = textBoxDemarcation2.Text;

                            Stack<UndoCommand> tempStack = new Stack<UndoCommand>();

                            // Pop all commands, skipping the ones we want to remove
                            while (redoStack.Count > 0)
                            {
                                var cmd = redoStack.Pop();

                                bool isTarget = cmd.Type == "Demarcation2"
                                    && cmd.Annotation.PageNumber == pageNum2 - 1
                                    && cmd.Annotation.Text == targetText;

                                if (!isTarget)
                                    tempStack.Push(cmd); // keep
                            }

                            // Rebuild original stack in correct order
                            while (tempStack.Count > 0)
                            {
                                redoStack.Push(tempStack.Pop());
                            }
                        }

                        UpdateUndoRedoButtons();
                        RenderPage(); //re render the page to reflect the changes
                        
                        MessageBox.Show("Old demarcation removed successfully.");

                        //reset the demarcation textbox & page textbox
                        textBoxDemarcation2.Text = string.Empty;
                        textBoxPage2.Text = string.Empty;

                        //wait what about undo and redo stacks?
                        //they will be updated automatically when we remove the demarcation from the actions list.
                        //so we don't need to do anything here.
                            

                    }
                    else
                    {
                        MessageBox.Show("No existing demarcation found to remove.");
                    }
                }
                else //if (result == DialogResult.Cancel)
                {
                    selectedAnnotationType = "Select";
                    SelectHandTool();
                    return; //looks like the user pressed cancel and wants to retain old values.
                }
            }

            selectedAnnotationType = "Demarcation2";
            pictureBox1.Cursor = Cursors.Cross;

            HighlightSelectedTool(btnDemarcation2);
        }

        private void btnAdd2Grid_Click(object sender, EventArgs e)
        {
            dataGridViewQuestions.Rows.Add(
            cbQUnit.Text,
            cbQSection.Text,
            cbQPart.Text,
            cbQNumber.Text,
            cbQType.Text,
            cbQMaxMarks.Text,
            textBoxDemarcation1.Text,
            textBoxPage1.Text,
            textBoxDemarcation2.Text,
            textBoxPage2.Text
            );
            // Clear the textboxes after adding to the grid
            textBoxDemarcation1.Clear();
            textBoxPage1.Clear();
            textBoxDemarcation2.Clear();
            textBoxPage2.Clear();
        }

        private void ApplyAssignedRegionAndPageFromRow(DataGridViewRow row)
        {
            string regionStr = row.Cells["ColumnDemarc1"].Value?.ToString();
            string pageNumStr = row.Cells["ColumnPageNo1"].Value?.ToString();

            if (!string.IsNullOrEmpty(regionStr))
            {
                string[] parts = regionStr.Split(',');
                if (parts.Length == 4 &&
                    float.TryParse(parts[0], out float x) &&
                    float.TryParse(parts[1], out float y) &&
                    float.TryParse(parts[2], out float w) &&
                    float.TryParse(parts[3], out float h))
                {
                    AssignedRegion = new RectangleF(x, y, w, h);
                }
            }

            if (int.TryParse(pageNumStr, out int page))
            {
                currentPage = page - 1;
                AssignedPageNumber = currentPage; // Store the assigned page number
                RenderPage(); // show correct page - 1! as the picturebox is 0-based
            }
        }
        private void AttachUndoHandlers(Control parent)
        {
            foreach (Control ctrl in parent.Controls)
            {
                if (ctrl is TextBox textBox)
                {
                    string oldVal = "";

                    textBox.Enter += (s, e) =>
                    { 
                        oldVal = textBox.Text; 
                    };
                    textBox.Leave += (s, e) =>
                    {
                        if (isUndoingOrRedoing) return;
                        //check if the control was actually edited
                        /* if (textBox.Tag != null)
                             if (textBox.Tag?.ToString() == "Undo" || textBox.Tag?.ToString() == "Redo" )
                                 return; // Not edited, skip*/

                        // Only trigger if the control was actually edited
                        if (oldVal != textBox.Text)
                        {
                                undoStack.Push(new UndoCommand
                            {
                                Type = "TextBox",
                                ControlName = textBox.Name,
                                OldValue = oldVal,
                                NewValue = textBox.Text
                            });
                            redoStack.Clear();
                            UpdateUndoRedoButtons();
                        }
                    };
                }
                else if (ctrl is ComboBox comboBox)
                {
                    string oldVal = "";
                    comboBox.Enter += (s, e) =>
                    {
                        oldVal = comboBox.Text;
                    };
                    comboBox.Leave += (s, e) =>
                    {
                        if (isUndoingOrRedoing) return;
                        //check if the control was actually edited
                        // If the tag is "False", it means it was not edited
                        // If the tag is null, it means it was not edited

                        /*if (comboBox.Tag != null)
                            if (comboBox.Tag.ToString() == "Undo" || comboBox.Tag.ToString() == "Redo")
                                return; // Not edited, skip*/

                        if (oldVal != comboBox.Text)
                        {
                            undoStack.Push(new UndoCommand
                            {
                                Type = "ComboBox",
                                ControlName = comboBox.Name,
                                OldValue = oldVal,
                                NewValue = comboBox.Text
                            });
                            redoStack.Clear();
                            UpdateUndoRedoButtons();
                        }
                    };
                }

                // Recursively attach to child controls (panels, groupboxes, etc.)
                if (ctrl.HasChildren)
                    AttachUndoHandlers(ctrl);
            }
        }
    }

}

