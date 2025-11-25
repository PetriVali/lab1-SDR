using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lab1_SDR
{
    class Doc
    {
        public string Id { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string Content { get; set; } = ""; 
        public List<string> TopicCodes { get; set; } = new();
        public string Sparse { get; set; } = "";
    }

}
