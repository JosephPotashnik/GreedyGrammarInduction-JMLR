// Copyright (c) 2026 Joseph Potashnik. Submitted for peer review to JMLR. Do not distribute or use without permission.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EarleyParserForGreedyGrammarInduction;
using GreedyGrammarInductionLearner;
using GreedyGrammarInductionLearner.SearchSpace;
using Microsoft.Extensions.Logging;
using ShellProgressBar;

namespace GreedyGrammarInduction
{
    public class ProgramParamsList
    {
        public ProgramParams[] ProgramsToRun { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DistributionType
    {
        Uniform = 0,
        Normal,
        PowerLaw
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SourceOfTruth
    {
        Grammar = 0,
        Samples
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LexiconSource
    {
        File = 0,
        IdentityFromTokens
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OracleComparisonMode
    {
        PartsOfSpeech = 0,
        SurfaceTokens
    }

    public class InputParams
    {
        public SourceOfTruth SourceOfTruth { get; set; } = SourceOfTruth.Grammar;
        public LexiconSource? LexiconSource { get; set; }
        public string GrammarFileName { get; set; }
        public string SamplesFileName { get; set; }
        public string LexiconFileName { get; set; }
        public int MaxSentenceLength { get; set; }
        public GrammarSamplingParams GrammarSamplingParams { get; set; }
    }

    public class SearchSpaceParams
    {
        public int MaxNumberOfNonTerminals { get; set; }
        public int MaxNumberOfRules { get; set; }
        public int MaxDepthBetweenSubSolutions { get; set; }
        public int MaxDepthAfterMinimalGlobal { get; set; }
        public int MinSizeOfOptimalSubSolution { get; set; }
        public double ParetoRibbonThickness { get; set; }
        public SearchSpaceHeuristicsParams SearchSpaceHeuristicsParams { get; set; } = new();
    }

    public class SearchSpaceHeuristicsParams
    {
        public bool ContinueSearchAfterOptimalNode { get; set; }
        public bool SkipKnownOptimalStructuralNodesAcrossRoots { get; set; }

        public SearchSpaceHeuristicsParams()
        {
            ContinueSearchAfterOptimalNode = true;
        }
    }


    public class GrammarSamplingParams
    {
        public double AllowedMissingProbabilityMass { get; set; }
        public DistributionType DistributionType { get; set; }
        public int? RandomSeed { get; set; }
        public string OutputSamplesToFile { get; set; }
    }

    public class PostLearnerVerification
    {
        public string ResultsContainGrammarWeaklyEquivalentTo { get; set; }
        public string ResultsContainGrammarStronglyEquivalentTo { get; set; }
        public OracleComparisonMode ComparisonMode { get; set; } = OracleComparisonMode.PartsOfSpeech;
        public string TargetLexiconFileName { get; set; }
    }

    public class ProgramParams
    {
        private PostLearnerVerification _postLearnerVerification;

        public InputParams InputParams { get; set; }
        public SearchSpaceParams SearchSpaceParams { get; set; }

        [JsonIgnore]
        public bool HasPostLearnerVerification { get; private set; }

        public PostLearnerVerification PostLearnerVerification
        {
            get => _postLearnerVerification;
            set
            {
                _postLearnerVerification = value;
                HasPostLearnerVerification = true;
            }
        }
    }


    public class Program
    {
        private const string DefaultProgramFileName = "QuickTestSuiteFixedSeed.json";

        private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static void Learn(string fileName, bool pauseBeforeExit)
        {
            if (fileName == null)
            {
                fileName = DefaultProgramFileName;
            }

            var programParamsList = ReadProgramParamsFromFile(fileName);
            var results = new List<(string GrammarName, int K, int M, int StopAfter, bool Success, TimeSpan Elapsed)>();

            for (int runIndex = 0; runIndex < programParamsList.ProgramsToRun.Length; runIndex++)
            {
                var programParams = programParamsList.ProgramsToRun[runIndex];
                SymbolTable.Init();
                var (success, elapsed) = RunProgram(programParams);
                results.Add((GetSelectedInputFileName(programParams),
                    programParams.SearchSpaceParams.MaxDepthBetweenSubSolutions,
                    programParams.SearchSpaceParams.MinSizeOfOptimalSubSolution,
                    programParams.SearchSpaceParams.MaxDepthAfterMinimalGlobal,
                    success, elapsed));
            }

            WriteRunReport(fileName, results);
            if (pauseBeforeExit && !Console.IsInputRedirected)
            {
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey(intercept: true);
            }
        }


        private static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            SetExecutionWorkingDirectory();
            string fileName = null;
            for (int i = 0; i < args.Length / 2; i++)
            {
                switch (args[i * 2])
                {
                    case @"FileName:":
                    case @"-FileName:":
                    case @"--FileName:":
                    {
                        fileName = args[(i * 2) + 1];
                        break;
                    }
                    default:
                        throw new Exception("unrecognized argument. Please use the following format: FileName: yourfilename.json (for example: NightRun.json");
                }
            }


            try
            {
                Process p = Process.GetCurrentProcess();
                p.PriorityClass = ProcessPriorityClass.High;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Some platforms, including ordinary WSL sessions, do not allow
                // raising process priority. Continue with the default priority.
            }

            Learn(fileName, pauseBeforeExit: args.Length == 0);

            //CheckDyck4EvidenceSufficiency();
        }

        private static void SetExecutionWorkingDirectory()
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }

        private static void CheckDyck4EvidenceSufficiency()
        {
            CheckEvidenceSufficiency(
                targetGrammarFileName: "Dyck4.txt",
                targetLexiconFileName: "ClassicalLexicon.json",
                samplesFileName: "Dyck4SMissingSamples.txt");
        }

        private static void CheckEvidenceSufficiency(
            string targetGrammarFileName,
            string targetLexiconFileName,
            string samplesFileName)
        {
            SymbolTable.Init();
            var symbolTable = SymbolTable.Instance;
            symbolTable.GetId(Grammar.StartSymbol);
            symbolTable.GetId(Grammar.GammaSymbol);
            symbolTable.GetId(Grammar.EpsilonSymbol);
            symbolTable.GetId(Grammar.StarSymbol);
            Grammar.s_symbolTable = symbolTable;
            GrammarExtensions.s_symbolTable = symbolTable;

            using var loggerFactory = LoggerFactory.Create(builder => builder
                .AddSimpleConsole()
                .AddFile("EvidenceSufficiency.txt", LogLevel.Debug));
            var logger = loggerFactory.CreateLogger(nameof(Services.EvidenceSufficiencyService));

            var targetLexicon = Lexicon.ReadLexiconFromFile(targetLexiconFileName);
            var (sentences, _) = Services.SentenceDataProcessor.ReadSamplesFromFile(samplesFileName, targetLexicon);

            var service = new Services.EvidenceSufficiencyService(logger);
            var response = service.CheckEnoughEvidence(new Services.EvidenceSufficiencyRequest
            {
                Sentences = sentences,
                TargetGrammarFileName = targetGrammarFileName,
                TargetLexicon = targetLexicon
            });

            Console.WriteLine($"Enough evidence to learn {targetGrammarFileName} from {samplesFileName}: {response.EnoughEvidenceToLearn}");
            Console.WriteLine($"Fitness: {response.Fitness}");
            Console.WriteLine(response.Message);
            if (response.MissingSurfaceSentences.Length > 0)
            {
                Console.WriteLine("Missing sentences to make the samples sufficient for learning the grammar:");
                foreach (var sentence in response.MissingSurfaceSentences)
                {
                    Console.WriteLine(string.IsNullOrEmpty(sentence) ? "<epsilon>" : sentence);
                }
            }
        }

        private static ILoggerFactory ConfigureLogger(ProgramParams programParams)
        {
            var fileName = GetLogFileName(programParams);

            return LoggerFactory.Create(builder => builder
                 .AddSimpleConsole()
                 .AddFile(fileName, LogLevel.Debug)
             );
        }

        private static string GetLogFileName(ProgramParams programParams)
        {
            var selectedInputFileName = GetSelectedInputFileName(programParams);
            var selectedInputName = Path.GetFileNameWithoutExtension(selectedInputFileName);
            return $"{selectedInputName}K{programParams.SearchSpaceParams.MaxDepthBetweenSubSolutions}.txt";
        }

        private static string GetSelectedInputFileName(ProgramParams programParams)
        {
            return programParams.InputParams.SourceOfTruth == SourceOfTruth.Samples
                ? programParams.InputParams.SamplesFileName
                : programParams.InputParams.GrammarFileName;
        }

        private static string ResolveOutputSamplesFileName(ProgramParams programParams)
        {
            var outputSamplesToFile = programParams.InputParams.GrammarSamplingParams?.OutputSamplesToFile;
            if (string.IsNullOrEmpty(outputSamplesToFile))
            {
                return null;
            }

            if (Path.IsPathRooted(outputSamplesToFile))
            {
                return outputSamplesToFile;
            }

            var logDirectory = Path.GetDirectoryName(GetLogFileName(programParams));
            return string.IsNullOrEmpty(logDirectory)
                ? outputSamplesToFile
                : Path.Combine(logDirectory, outputSamplesToFile);
        }

        private static void ValidateInputParams(ProgramParams programParams)
        {
            if (programParams.InputParams == null)
            {
                throw new InvalidOperationException("InputParams is required.");
            }

            var lexiconSource = programParams.InputParams.LexiconSource;
            if (lexiconSource.HasValue &&
                lexiconSource.Value != LexiconSource.File &&
                lexiconSource.Value != LexiconSource.IdentityFromTokens)
            {
                throw new InvalidOperationException($"Unrecognized lexicon source: {lexiconSource.Value}");
            }

            switch (programParams.InputParams.SourceOfTruth)
            {
                case SourceOfTruth.Grammar:
                    if (programParams.InputParams.LexiconSource != LexiconSource.File)
                    {
                        throw new InvalidOperationException("SourceOfTruth Grammar requires LexiconSource File because grammar-driven sentence generation needs a POS-to-token lexicon.");
                    }
                    if (string.IsNullOrEmpty(programParams.InputParams.LexiconFileName))
                    {
                        throw new InvalidOperationException("LexiconFileName is required when SourceOfTruth is Grammar.");
                    }
                    if (string.IsNullOrEmpty(programParams.InputParams.GrammarFileName))
                    {
                        throw new InvalidOperationException("GrammarFileName is required when SourceOfTruth is Grammar.");
                    }
                    if (programParams.InputParams.GrammarSamplingParams == null)
                    {
                        throw new InvalidOperationException("GrammarSamplingParams is required when SourceOfTruth is Grammar.");
                    }
                    break;
                case SourceOfTruth.Samples:
                    if (programParams.InputParams.LexiconSource == null)
                    {
                        throw new InvalidOperationException("LexiconSource is required when SourceOfTruth is Samples.");
                    }
                    if (string.IsNullOrEmpty(programParams.InputParams.SamplesFileName))
                    {
                        throw new InvalidOperationException("SamplesFileName is required when SourceOfTruth is Samples.");
                    }
                    if (programParams.InputParams.LexiconSource == LexiconSource.File &&
                        string.IsNullOrEmpty(programParams.InputParams.LexiconFileName))
                    {
                        throw new InvalidOperationException("LexiconFileName is required when LexiconSource is File.");
                    }
                    if (programParams.InputParams.LexiconSource == LexiconSource.IdentityFromTokens &&
                        !string.IsNullOrEmpty(programParams.InputParams.LexiconFileName))
                    {
                        throw new InvalidOperationException("LexiconFileName must be omitted when LexiconSource is IdentityFromTokens.");
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unrecognized source of truth: {programParams.InputParams.SourceOfTruth}");
            }

            var weakEquivalenceTargetFileName =
                programParams.PostLearnerVerification?.ResultsContainGrammarWeaklyEquivalentTo
                ?? programParams.InputParams.GrammarFileName;
            if (programParams.HasPostLearnerVerification && programParams.PostLearnerVerification != null && string.IsNullOrEmpty(weakEquivalenceTargetFileName))
            {
                throw new InvalidOperationException("ResultsContainGrammarWeaklyEquivalentTo is required when PostLearnerVerification is present.");
            }

            if (programParams.HasPostLearnerVerification &&
                programParams.InputParams.SourceOfTruth == SourceOfTruth.Samples &&
                programParams.InputParams.LexiconSource == LexiconSource.IdentityFromTokens)
            {
                if (programParams.PostLearnerVerification.ComparisonMode != OracleComparisonMode.SurfaceTokens)
                {
                    throw new InvalidOperationException("PostLearnerVerification with SourceOfTruth Samples and LexiconSource IdentityFromTokens requires ComparisonMode SurfaceTokens.");
                }

                if (string.IsNullOrEmpty(programParams.PostLearnerVerification.TargetLexiconFileName))
                {
                    throw new InvalidOperationException("TargetLexiconFileName is required for PostLearnerVerification when samples use IdentityFromTokens.");
                }
            }
        }

        public static void StopWatch(Stopwatch stopWatch, ILogger logger)
        {
            stopWatch.Stop();
            var ts = stopWatch.Elapsed;
            var elapsedTime = $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
            logger.LogInformation("Overall session RunTime " + elapsedTime);
        }


        internal static (bool Success, TimeSpan Elapsed) RunProgram(ProgramParams programParams)
        {
            ValidateInputParams(programParams);
            Grammar.PartsOfSpeech = new HashSet<int>();
            using var loggerFactory = ConfigureLogger(programParams);
            var logger = loggerFactory.CreateLogger(nameof(GreedyGrammarInduction));
            bool runPostLearnerVerification = programParams.HasPostLearnerVerification && programParams.PostLearnerVerification != null;

            var s = "------------------------------------------------------------\r\n" +
                    $"Session {DateTime.Now:MM/dd/yyyy h:mm tt}\r\n";
            var json = JsonSerializer.Serialize(programParams, s_jsonSerializerOptions);
            logger.LogInformation(json);

            // Initialize symbol table with special symbols FIRST (to match master branch order)
            var symbolTable = SymbolTable.Instance;
            var startId = symbolTable.GetId(Grammar.StartSymbol);
            var gammaId = symbolTable.GetId(Grammar.GammaSymbol);
            var epsilonId = symbolTable.GetId(Grammar.EpsilonSymbol);
            var starId = symbolTable.GetId(Grammar.StarSymbol);
            Grammar.s_symbolTable = symbolTable;
            GreedyGrammarInductionLearner.SearchSpace.GrammarExtensions.s_symbolTable = symbolTable;

            var stopWatch = Stopwatch.StartNew();

            // Read grammar and lexicon data. POS symbols are registered by the selected lexicon source.
            Lexicon universalLexicon = null;
            SentenceWithCounts[] sentences;
            Lexicon dataLexicon;

            if (programParams.InputParams.SourceOfTruth == SourceOfTruth.Grammar)
            {
                universalLexicon = Lexicon.ReadLexiconFromFile(programParams.InputParams.LexiconFileName);
                var grammarSamplingParams = programParams.InputParams.GrammarSamplingParams;
                var grammarRules = GrammarFileReader.ReadRulesFromFile(programParams.InputParams.GrammarFileName);

                // Use the new SentenceGenerationService
                var sentenceService = new Services.SentenceGenerationService(logger);
                var request = new Services.SentenceGenerationRequest
                {
                    GrammarRules = grammarRules,
                    UniversalLexicon = universalLexicon,
                    DistributionType = grammarSamplingParams.DistributionType,
                    MaxSentenceLength = programParams.InputParams.MaxSentenceLength,
                    AllowedMissingProbabilityMass = grammarSamplingParams.AllowedMissingProbabilityMass,
                    NumberOfSamples = 1000,
                    RandomSeed = grammarSamplingParams.RandomSeed,
                    OutputSamplesToFile = ResolveOutputSamplesFileName(programParams),
                    GrammarFileName = programParams.InputParams.GrammarFileName
                };

                // Generate sentences
                var sentenceResponse = sentenceService.GenerateSentences(request);
                sentences = sentenceResponse.Sentences;
                dataLexicon = sentenceResponse.DataLexicon;
            }
            else if (programParams.InputParams.SourceOfTruth == SourceOfTruth.Samples)
            {
                if (programParams.InputParams.LexiconSource == LexiconSource.File)
                {
                    logger.LogWarning("Using file lexicon with sample input: the lexicon is treated as fixed prior knowledge and is not learned from samples. Sample tokens are validated against the file lexicon and unknown tokens fail the run.");
                    universalLexicon = Lexicon.ReadLexiconFromFile(programParams.InputParams.LexiconFileName);
                    (sentences, dataLexicon) = Services.SentenceDataProcessor.ReadSamplesFromFile(
                        programParams.InputParams.SamplesFileName,
                        universalLexicon);
                }
                else if (programParams.InputParams.LexiconSource == LexiconSource.IdentityFromTokens)
                {
                    logger.LogInformation("Using identity lexicon from sample tokens: each distinct sample token is treated as its own POS/preterminal.");
                    (sentences, dataLexicon) = Services.SentenceDataProcessor.ReadSamplesFromFileWithIdentityLexicon(
                        programParams.InputParams.SamplesFileName);
                    Grammar.PartsOfSpeech = new HashSet<int>(dataLexicon.POSWithPossibleWords.Keys);
                }
                else
                {
                    throw new InvalidOperationException($"Unrecognized lexicon source: {programParams.InputParams.LexiconSource}");
                }

                EarleyParser.ScannedRules = new ScannedRulesDict(dataLexicon).ScannedRules;
                logger.LogInformation(
                    "Loaded {UniqueSampleCount} unique sampled sentences from {SamplesFileName}",
                    sentences.Length,
                    programParams.InputParams.SamplesFileName);
            }
            else
            {
                throw new InvalidOperationException($"Unrecognized source of truth: {programParams.InputParams.SourceOfTruth}");
            }


            // Insert all Xi nonterminals into symbol table (for learner)
            // IMPORTANT: This must happen AFTER sentence generation or sample loading registers POS symbols.
            for (int i = 0; i < programParams.SearchSpaceParams.MaxNumberOfNonTerminals; i++)
            {
                symbolTable.GetId($"X{i + 1}");
            }

            // Learn grammar from the generated data using the new service
            var learnerService = new Services.GrammarLearnerService(logger, CreateProgressBar);
            var learningRequest = new Services.GrammarLearningRequest
            {
                Sentences = sentences,
                DataLexicon = dataLexicon,
                SearchSpaceParams = programParams.SearchSpaceParams,
                ResultsContainGrammarWeaklyEquivalentTo = runPostLearnerVerification ? programParams.PostLearnerVerification?.ResultsContainGrammarWeaklyEquivalentTo : null,
                ResultsContainGrammarStronglyEquivalentTo = runPostLearnerVerification ? programParams.PostLearnerVerification?.ResultsContainGrammarStronglyEquivalentTo : null,
                VerificationComparisonMode = runPostLearnerVerification ? programParams.PostLearnerVerification.ComparisonMode : OracleComparisonMode.PartsOfSpeech,
                VerificationTargetLexiconFileName = runPostLearnerVerification ? programParams.PostLearnerVerification?.TargetLexiconFileName : null,
                RunPostLearnerVerification = runPostLearnerVerification
            };

            var learningResponse = learnerService.LearnGrammar(learningRequest);

            StopWatch(stopWatch, logger);
            return (learningResponse.Success, stopWatch.Elapsed);
        }

#pragma warning disable CA2000 // Dispose objects before losing scope
#pragma warning disable CA1859 // Use concrete types when possible for improved performance
        private static IProgress<double> CreateProgressBar(string title) => new Services.ShellProgress(new ProgressBar(maxTicks: 100, title));
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
#pragma warning restore CA2000 // Dispose objects before losing scope

        private static void WriteRunReport(string batchFileName, List<(string GrammarName, int K, int M, int StopAfter, bool Success, TimeSpan Elapsed)> results)
        {
            var timestamp = DateTime.Now;
            var reportFileName = $"RunReport_{timestamp:yyyy-MM-dd_HH-mm-ss}.txt";

            var header = $"  {"Name",-45} {"k",2} {"m",2} {"stop after",10}  {"Status",-6}  {"Duration",8}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Run Report - {timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Batch file: {batchFileName}");
            sb.AppendLine(new string('-', 80));
            sb.AppendLine(header);
            sb.AppendLine(new string('-', 80));

            int passed = 0, failed = 0;
            foreach (var (grammarName, k, m, stopAfter, success, elapsed) in results)
            {
                var status = success ? "PASSED" : "FAILED";
                sb.AppendLine($"  {grammarName,-45} {k,2} {m,2} {stopAfter,10}  {status,-6}  ({elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00})");
                if (success) passed++; else failed++;
            }

            sb.AppendLine(new string('-', 80));
            sb.AppendLine($"Total: {results.Count}  Passed: {passed}  Failed: {failed}");

            // Write to file
            File.WriteAllText(reportFileName, sb.ToString());

            // Print to console with colors
            Console.WriteLine();
            Console.WriteLine($"Run Report - {timestamp:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Batch file: {batchFileName}");
            Console.WriteLine(new string('-', 80));
            Console.WriteLine(header);
            Console.WriteLine(new string('-', 80));

            foreach (var (grammarName, k, m, stopAfter, success, elapsed) in results)
            {
                Console.ForegroundColor = success ? ConsoleColor.Green : ConsoleColor.Red;
                var status = success ? "PASSED" : "FAILED";
                Console.WriteLine($"  {grammarName,-45} {k,2} {m,2} {stopAfter,10}  {status,-6}  ({elapsed.Hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00})");
            }
            Console.ResetColor();

            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"Total: {results.Count}  Passed: {passed}  Failed: {failed}");
            Console.WriteLine($"Report saved to: {reportFileName}");
        }

        internal static ProgramParamsList ReadProgramParamsFromFile(string fileName)
        {
            fileName = Path.Combine([".", "InputData", "ProgramsToRun", fileName]);
            using var file = File.OpenRead(fileName);
            var programParamsList = JsonSerializer.Deserialize<ProgramParamsList>(file, s_jsonSerializerOptions);
            return programParamsList;
        }
    }
}
