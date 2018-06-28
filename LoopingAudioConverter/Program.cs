﻿using LoopingAudioConverter.VGAudio;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VGAudio.Containers.NintendoWare;

namespace LoopingAudioConverter {
	class Program {
		[STAThread]
		static void Main(string[] args) {
			Application.EnableVisualStyles();

			bool appsettingserror = false;
			foreach (string s in new string[] { "sox_path", "madplay_path", "vgmstream_path", "lame_path", "faad_path" }) {
				string v = ConfigurationManager.AppSettings[s];
				if (string.IsNullOrEmpty(v)) {
					appsettingserror = true;
					Console.Error.WriteLine("The configuration setting " + s + " is missing.");
				} else if (!File.Exists(ConfigurationManager.AppSettings[s])) {
					appsettingserror = true;
					Console.Error.WriteLine("Could not find " + s + ": " + v);
				}
			}

			if (appsettingserror) {
				MessageBox.Show("One or more programs could not be found; the program may not run properly. See the console for details.");
			}

			string ini = null;
			List<string> initialInputFiles = new List<string>();
			bool auto = false;
			foreach (string arg in args) {
				if (arg == "--auto") {
					auto = true;
				} else if (Path.GetExtension(arg).ToLowerInvariant() == ".ini") {
					if (ini != null) {
						throw new Exception("You cannot specify more than one .ini file.");
					}
					ini = arg;
				} else {
					initialInputFiles.Add(arg);
				}
			}

			OptionsForm f = new OptionsForm();
			if (File.Exists(ini ?? "LoopingAudioConverter.ini")) {
				f.LoadOptions(ini ?? "LoopingAudioConverter.ini");
			}

			f.AddInputFiles(initialInputFiles);
			
			if (auto) {
				Run(f.GetOptions(), showEndDialog: false);
			} else {
				Application.Run(f);
				Task.WaitAll(f.RunningTasks.ToArray());
			}
		}

		/// <summary>
		/// Runs a batch conversion process.
		/// </summary>
		/// <param name="o">Options for the batch.</param>
		public static void Run(Options o, bool showEndDialog = true) {
			if (o.ExporterType == ExporterType.MP3 && (o.ExportPreLoop || o.ExportLoop)) {
				MessageBox.Show("MP3 encoding adds gaps at the start and end of each file, so the before-loop portion and the loop portion will not line up well.",
					"Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}

			SoX sox = new SoX(ConfigurationManager.AppSettings["sox_path"]);
			WAVExporter.txt = "";

			List<IAudioImporter> importers = new List<IAudioImporter> {
					new WAVImporter(),
					new MP3Importer(ConfigurationManager.AppSettings["madplay_path"]),
					new MP4Importer(ConfigurationManager.AppSettings["faad_path"]),
					new VGMImporter(ConfigurationManager.AppSettings["vgmplay_path"] ?? ConfigurationManager.AppSettings["vgm2wav_path"]),
					new MSU1(),
					new VGMStreamImporter(ConfigurationManager.AppSettings["vgmstream_path"]),
					sox
				};
			if (o.VGAudioDecoder) {
				importers.Insert(1, new VGAudioImporter());
			}

			IAudioExporter exporter;
			switch (o.ExporterType) {
				case ExporterType.BRSTM:
					exporter = new RSTMExporter(o.BxstmCodec);
					break;
				case ExporterType.BCSTM:
					exporter = new CSTMExporter(o.BxstmCodec);
					break;
				case ExporterType.BFSTM_Cafe:
					exporter = new FSTMExporter(o.BxstmCodec, NwTarget.Cafe);
					break;
				case ExporterType.BFSTM_NX:
					exporter = new FSTMExporter(o.BxstmCodec, NwTarget.NX);
					break;
				case ExporterType.DSP:
					exporter = new DSPExporter();
					break;
				case ExporterType.IDSP:
					exporter = new IDSPExporter();
					break;
				case ExporterType.HCA:
					exporter = new HCAExporter();
					break;
				case ExporterType.HPS:
					exporter = new HPSExporter();
					break;
				case ExporterType.BRSTM_BrawlLib:
					exporter = new Brawl.RSTMExporter();
					break;
				case ExporterType.BCSTM_BrawlLib:
					exporter = new Brawl.CSTMExporter();
					break;
				case ExporterType.BFSTM_BrawlLib:
					exporter = new Brawl.FSTMExporter();
					break;
				case ExporterType.MSU1:
					exporter = new MSU1();
					break;
				case ExporterType.FLAC:
					exporter = new FLACExporter(sox);
					break;
				case ExporterType.MP3:
					exporter = new MP3Exporter(ConfigurationManager.AppSettings["lame_path"], o.MP3EncodingParameters);
					break;
				case ExporterType.AAC_M4A:
					exporter = new AACExporter(ConfigurationManager.AppSettings["qaac_path"], o.AACEncodingParameters, adts: false);
					break;
				case ExporterType.AAC_ADTS:
					exporter = new AACExporter(ConfigurationManager.AppSettings["qaac_path"], o.AACEncodingParameters, adts: true);
					break;
				case ExporterType.OggVorbis:
					exporter = new OggVorbisExporter(sox, o.OggVorbisEncodingParameters);
					break;
				case ExporterType.WAV:
					exporter = new WAVExporter();
					break;
				default:
					throw new Exception("Could not create exporter type " + o.ExporterType);
			}

			List<Task> tasks = new List<Task>();
			Semaphore sem = new Semaphore(o.NumSimulTasks, o.NumSimulTasks);

			MultipleProgressWindow window = new MultipleProgressWindow();
			new Thread(new ThreadStart(() => {
				Application.EnableVisualStyles();
				window.ShowDialog();
			})).Start();

			Dictionary<string, Tuple<int, int, int>> loopOverrides = new Dictionary<string, Tuple<int, int, int>>();
			if (File.Exists("loop.txt")) {
				using (StreamReader sr = new StreamReader("loop.txt")) {
					string line;
					while ((line = sr.ReadLine()) != null) {
						line = Regex.Replace(line, "[ \t]+", " ");
						if (line.Length > 0 && line[0] != '#' && line.Contains(' ')) {
							try {
								int loopStart = int.Parse(line.Substring(0, line.IndexOf(" ")));
								line = line.Substring(line.IndexOf(" ") + 1);
								int loopEnd = int.Parse(line.Substring(0, line.IndexOf(" ")));
								line = line.Substring(line.IndexOf(" ") + 1);
								int samplerate = int.Parse(line.Substring(0, line.IndexOf(" ")));
								line = line.Substring(line.IndexOf(" ") + 1);

								loopOverrides.Add(line, new Tuple<int, int, int>(loopStart, loopEnd, samplerate));
							} catch (Exception e) {
								Console.Error.WriteLine("Could not parse line in loop.txt: " + line + " - " + e.Message);
							}
						}
					}
				}
			}

			if (!o.InputFiles.Any()) {
				MessageBox.Show("No input files were selected.");
			}

			int i = 0;
			float maxProgress = o.InputFiles.Count() * 2;

			List<string> exported = new List<string>();
			DateTime start = DateTime.UtcNow;
			foreach (string inputFile in o.InputFiles) {
				sem.WaitOne();
				if (window.Canceled) break;

				string outputDir = o.OutputDir;
				string inputDir = Path.GetDirectoryName(inputFile);
				for (int x = 0; x < 100; x++) {
					int index = outputDir.LastIndexOf('*');
					if (index < 0) break;

					string replacement = Path.GetFileName(inputDir);
					outputDir = outputDir.Substring(0, index) + replacement + outputDir.Substring(index + 1);
					inputDir = Path.GetDirectoryName(inputDir);
				}

				if (!Directory.Exists(outputDir)) {
					try {
						Directory.CreateDirectory(outputDir);
					} catch (Exception e) {
						MessageBox.Show("Could not create output directory " + o.OutputDir + ": " + e.Message);
					}
				}

				string filename_no_ext = Path.GetFileNameWithoutExtension(inputFile);
				window.SetDecodingText(filename_no_ext);

				string extension = Path.GetExtension(inputFile);

				PCM16Audio w = null;
				List<AudioImporterException> exceptions = new List<AudioImporterException>();

				var importers_supported = importers.Where(im => im.SupportsExtension(extension));
				if (!importers_supported.Any()) {
					throw new Exception("No importers supported for file extension " + extension);
				}

				foreach (IAudioImporter importer in importers_supported) {
					try {
						Console.WriteLine("Decoding " + Path.GetFileName(inputFile) + " with " + importer.GetImporterName());
						if (importer is IRenderingAudioImporter) {
							((IRenderingAudioImporter)importer).SampleRate = o.MaxSampleRate;
						}
						w = importer.ReadFile(inputFile);
						w.OriginalPath = inputFile;
						break;
					} catch (AudioImporterException e) {
						//Console.Error.WriteLine(importer.GetImporterName() + " could not read file " + inputFile + ": " + e.Message);
						exceptions.Add(e);
					}
				}

				if (loopOverrides.Any()) {
					if (loopOverrides.TryGetValue(Path.GetFileName(inputFile), out Tuple<int, int, int> val)) {
						if (val.Item1 < 0) {
							w.Looping = false;
						} else {
							w.Looping = true;
							w.LoopStart = (int)((float)val.Item1 / val.Item3 * (w.Samples.Length / w.Channels)); // use floats for division accuracy, then round back
							w.LoopEnd = (int)((float)val.Item2 / val.Item3 * (w.Samples.Length / w.Channels));
						}
					}
				}

				if (w == null) {
					window.SetDecodingText("");
					DialogResult dr = MessageBox.Show("Could not read " + inputFile + ".", "Error", MessageBoxButtons.OKCancel, MessageBoxIcon.Error);
					if (dr == DialogResult.Cancel) {
						break;
					} else {
						continue;
					}
				}

				window.SetDecodingText(filename_no_ext + " (applying effects)");
				w = sox.ApplyEffects(w,
					max_channels: o.MaxChannels ?? int.MaxValue,
					max_rate: o.MaxSampleRate ?? int.MaxValue,
					db: o.AmplifydB ?? 0M,
					amplitude: o.AmplifyRatio ?? 1M);
				window.SetDecodingText("");

				List<NamedAudio> wavsToExport = new List<NamedAudio>();

				if (o.ExportWholeSong) wavsToExport.Add(new NamedAudio(w.PlayLoopAndFade(o.NumberOfLoops, o.FadeOutSec), filename_no_ext + o.WholeSongSuffix));
				if (o.ExportPreLoop) wavsToExport.Add(new NamedAudio(w.GetPreLoopSegment(), filename_no_ext + o.PreLoopSuffix));
				if (o.ExportLoop) wavsToExport.Add(new NamedAudio(w.GetLoopSegment(), filename_no_ext + o.LoopSuffix));

				if (o.ChannelSplit == ChannelSplit.Pairs) wavsToExport = wavsToExport.SelectMany(x => x.SplitMultiChannelToStereo()).ToList();
				if (o.ChannelSplit == ChannelSplit.Each) wavsToExport = wavsToExport.SelectMany(x => x.SplitMultiChannelToMono()).ToList();

				sem.Release();

				foreach (NamedAudio n in wavsToExport) {
					NamedAudio toExport = n;
					int claims = 1;
					if (exporter is VGAudioExporter) {
						// VGAudio runs tasks in parallel for each channel, so let's consider that when deciding how many tasks to run.
						claims = Math.Min(n.LWAV.Channels, o.NumSimulTasks);
					}
					for (int j = 0; j < claims; j++) {
						sem.WaitOne();
					}
					switch (o.UnknownLoopBehavior) {
						case UnknownLoopBehavior.ForceLoop:
							if (!toExport.LWAV.Looping && !toExport.LWAV.NonLooping) {
								toExport.LWAV.Looping = true;
								toExport.LWAV.LoopStart = 0;
								toExport.LWAV.LoopEnd = toExport.LWAV.Samples.Length / toExport.LWAV.Channels;
							}
							break;
						case UnknownLoopBehavior.Ask:
						case UnknownLoopBehavior.AskAll:
							if (toExport.LWAV.Looping || toExport.LWAV.NonLooping) {
								if (o.UnknownLoopBehavior != UnknownLoopBehavior.AskAll) {
									break;
								}
							}
							PCM16LoopWrapper audioStream = new PCM16LoopWrapper(toExport.LWAV);
							using (BrstmConverterDialog dialog = new BrstmConverterDialog(audioStream)) {
								dialog.AudioSource = n.Name;
								if (dialog.ShowDialog() != DialogResult.OK) {
									toExport = null;
								}
							}
							break;
					}
					if (toExport == null) {
						i++;
						break;
					}

					if (!o.ShortCircuit) {
						if (toExport.LWAV.OriginalPath != null) {
							toExport.LWAV.OriginalPath = null;
						}
						if (toExport.LWAV.OriginalAudioData != null) {
							toExport.LWAV.OriginalAudioData = null;
						}
					}
					if (!o.WriteLoopingMetadata) {
						toExport.LWAV.Looping = false;
					}

					var row = window.AddEncodingRow(toExport.Name);
					//if (o.NumSimulTasks == 1) {
					//    exporter.WriteFile(toExport.LWAV, outputDir, toExport.Name);
					//    lock (exported) {
					//        exported.Add(toExport.Name);
					//    }
					//    for (int j = 0; j < claims; j++) {
					//        sem.Release();
					//    }
					//    row.Remove();
					//} else {
					Task task = new Task(() => exporter.WriteFile(toExport.LWAV, outputDir, toExport.Name));
					task.Start();
					tasks.Add(task);
					tasks.Add(task.ContinueWith(t => {
						lock (exported) {
							exported.Add(toExport.Name);
						}
						for (int j = 0; j < claims; j++) {
							sem.Release();
						}
						row.Remove();
					}));
					//}
				}
			}
			foreach (var t in tasks) {
				try {
					t.Wait();
				} catch (Exception ex) {
					Console.Error.WriteLine($"{ex.GetType()}: {ex.Message}");
					Console.Error.WriteLine(ex.StackTrace);
					MessageBox.Show((ex.InnerException ?? ex).Message);
				}
			}
			DateTime end = DateTime.UtcNow;

			if (window.Visible) window.BeginInvoke(new Action(() => {
				window.AllowClose = true;
				window.Close();
			}));

			File.WriteAllText("./inloop.txt", WAVExporter.txt);

			if (showEndDialog) {
				MessageBox.Show("Exported " + exported.Count + " file(s), total time: " + (end - start));
			}
		}
	}
}
