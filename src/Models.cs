using System;
using System.Collections.Generic;

namespace PdfSplitter.Function {

    public class Result {

        public Result(IList<string> newFiles) {
            this.files = newFiles;
        }

        public IList<string> files { get; private set; }
    }

    public class BatchRequest {

        public string url { get; set; }
    }

    public class BatchResult {

        public string status { get; set; }

        public RecognitionResult[] recognitionResults { get; set; }
    }

    public class RecognitionResult {

        public int page { get; set; }

        public float clockwiseOrientation { get; set; }

        public float width { get; set; }

        public float height { get; set; }

        public string unit { get; set; }

        public Line[] lines { get; set; }
    }

    public class Line {

        public float[] boundingBox { get; set; }

        public string text { get; set; }

        public Word[] words { get; set; }
    }

    public class Word {

        public float[] boundingBox { get; set; }

        public string text { get; set; }

        public string confidence { get; set; }
    }
}
