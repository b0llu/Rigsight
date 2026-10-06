using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Rigsight.Core.Ask;

namespace Rigsight.Services;

/// <summary>
/// The small language model behind Ask (all-MiniLM-L6-v2, 23 MB, run on the CPU by ONNX Runtime): it turns a sentence
/// into 384 numbers so that sentences that mean alike come out close together. It only reads questions; it writes
/// nothing. Loaded on the first question (a fifth of a second) and let go again when Ask hasn't been used for a while.
/// </summary>
public sealed class AskEmbedder : IAskEmbedder, IDisposable
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "ask");
    public static string ModelFile => Path.Combine(Folder, "model.onnx");
    public static string VocabFile => Path.Combine(Folder, "vocab.txt");

    /// <summary>The model's files are in place (they ship with the app).</summary>
    public static bool FilesPresent => File.Exists(ModelFile) && File.Exists(VocabFile);

    /// <summary>The model can be used: its files are there, and so is the Microsoft runtime that runs it.</summary>
    public static bool Available => FilesPresent && RuntimeReady;

    /// <summary>The oldest Visual C++ runtime ONNX Runtime is safe with (older copies have crashed it on load).</summary>
    private static readonly Version MinRuntime = new(14, 40);

    private static bool? _runtimeReady;

    /// <summary>
    /// ONNX Runtime needs the Visual C++ runtime (four files in System32), which Windows doesn't include and Rigsight
    /// doesn't ship: most PCs have it from a game or another app, some don't, and some have an old one. Checked before
    /// anything of ONNX Runtime is loaded, so a missing or old copy means questions are read by their words alone,
    /// never a crash.
    /// </summary>
    public static bool RuntimeReady => _runtimeReady ??= CheckRuntime(Environment.SystemDirectory);

    internal static bool CheckRuntime(string systemFolder)
    {
        try
        {
            foreach (string name in new[] { "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            {
                string file = Path.Combine(systemFolder, name);
                if (!File.Exists(file)) return false;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
                if (new Version(info.FileMajorPart, info.FileMinorPart) < MinRuntime) return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Rigsight.Core.Log.Error("ask", ex);
            return false;
        }
    }

    private readonly Lock _gate = new();
    private InferenceSession? _session;
    private WordPiece? _tokens;

    /// <summary>The vocabulary alone (a quarter of a megabyte, read in a few milliseconds): no model is loaded for it.</summary>
    private WordPiece Tokens() => _tokens ??= new WordPiece(File.ReadLines(VocabFile));

    public bool IsWord(string word)
    {
        lock (_gate) return Tokens().Contains(word);
    }

    private (InferenceSession Session, WordPiece Tokens) Open()
    {
        if (_session is not null && _tokens is not null) return (_session, _tokens);
        Tokens();
        // Two threads at most: a question takes a millisecond or two, and a game may be running.
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(ModelFile, options);
        return (_session, _tokens!);
    }

    public float[][] Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        lock (_gate)
        {
            var (session, tokens) = Open();
            var encoded = texts.Select(tokens.Encode).ToList();
            int n = encoded.Count, length = encoded.Max(e => e.Length);
            var ids = new DenseTensor<long>([n, length]);
            var mask = new DenseTensor<long>([n, length]);
            var types = new DenseTensor<long>([n, length]);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < encoded[i].Length; j++)
                {
                    ids[i, j] = encoded[i][j];
                    mask[i, j] = 1;
                }

            using var result = session.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", types)]);
            var hidden = result[0].AsTensor<float>();
            int size = hidden.Dimensions[2];

            // A sentence is the average of its words' vectors, scaled to length 1.
            var vectors = new float[n][];
            for (int i = 0; i < n; i++)
            {
                var v = new float[size];
                for (int j = 0; j < encoded[i].Length; j++)
                    for (int k = 0; k < size; k++) v[k] += hidden[i, j, k];
                double norm = 0;
                foreach (float x in v) norm += (double)x * x;
                norm = Math.Sqrt(norm);
                if (norm > 0)
                    for (int k = 0; k < size; k++) v[k] = (float)(v[k] / norm);
                vectors[i] = v;
            }
            return vectors;
        }
    }

    /// <summary>Lets the model go (about 60 MB); the next question loads it again.</summary>
    public void Unload()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    public void Dispose() => Unload();
}
