using System.IO;
using System.Threading.Tasks;
using VGAudio.Formats;

namespace LoopingAudioConverter {
	public class WAVExporter : IAudioExporter {
		public static string txt = "";

		public void WriteFile(PCM16Audio lwav, string output_dir, string original_filename_no_ext) {
			string output_filename = Path.Combine(output_dir, original_filename_no_ext + ".wav");
			int length = lwav.Samples.Length / lwav.Channels;
			string filename = Path.GetFileNameWithoutExtension(lwav.OriginalPath);
			if (lwav.Looping)
				if(!txt.Contains(filename))
					txt += $"{lwav.LoopStart} {lwav.LoopEnd} {lwav.Samples.Length / lwav.Channels} {Path.GetFileNameWithoutExtension(lwav.OriginalPath)}.wav\r\n";
			File.WriteAllBytes(output_filename, lwav.Export());
		}

		public Task WriteFileAsync(PCM16Audio lwav, string output_dir, string original_filename_no_ext) {
			Task t = new Task(() => WriteFile(lwav, output_dir, original_filename_no_ext));
			t.Start();
			return t;
		}

		public string GetExporterName() {
			return "LWAVExporter";
		}
	}
}
