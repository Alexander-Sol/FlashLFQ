﻿using Chemistry;
using IO.MzML;
using IO.Thermo;
using MassSpectrometry;
using Proteomics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UsefulProteomicsDatabases;

namespace FlashLFQ
{
    class FlashLfqEngine
    {
        // file info stuff
        private string identificationsFilePath;
        public string[] filePaths { get; private set; }

        // structures used in the FlashLFQ program
        private List<Identification> allIdentifications;
        private List<Feature> allFeatures;
        private Dictionary<string, List<KeyValuePair<double, double>>> baseSequenceToIsotopicDistribution;
        private string[] featureOutputHeader;

        // settings
        public bool silent { get; private set; }
        public bool pause { get; private set; }
        public int maxDegreesOfParallelism { get; private set; }
        private IEnumerable<int> chargeStates;
        private double ppmTolerance;
        private double rtTolerance;
        private double isotopePpmTolerance;
        private bool integrate;
        private bool sumFeatures;
        private double initialRTWindow;
        private int missedScansAllowed;
        private int numberOfIsotopesToLookFor;
        private double signalToBackgroundRequired;
        private double mbrSignalToBackgroundRequired;
        private double mbrRtWindow;
        private bool filterUnreliableMatches;
        private bool mbr;
        
        public FlashLfqEngine()
        {
            allIdentifications = new List<Identification>();
            allFeatures = new List<Feature>();
            baseSequenceToIsotopicDistribution = new Dictionary<string, List<KeyValuePair<double, double>>>();

            // default parameters
            signalToBackgroundRequired = 5.0;
            mbrSignalToBackgroundRequired = 20.0;
            mbrRtWindow = 5.0;
            chargeStates = new List<int>();
            ppmTolerance = 10.0;
            rtTolerance = 3.0;
            isotopePpmTolerance = 3.0;
            integrate = false;
            sumFeatures = false;
            initialRTWindow = 0.4;
            missedScansAllowed = 1;
            numberOfIsotopesToLookFor = 2;
            silent = false;
            pause = true;
            filterUnreliableMatches = false;
            mbr = true;
            maxDegreesOfParallelism = 3;
        }

        public bool ParseArgs(string[] args)
        {
            string[] validArgs = new string[] { "--idt [string|identification file path (TSV format)]",
                "--raw [string|MS data file (.raw or .mzml)]", "--rep [string|directory containing MS data files]", "--ppm [double|ppm tolerance]",
                "--ret [double|retention time window]", "--iso [double|isotopic distribution tolerance in ppm]", "--sil [bool|silent mode]",
                "--pau [bool|pause at end of run]", "--int [bool|integrate features]", "--sum [bool|sum features in a run]" };
            var newargs = string.Join("", args).Split(new[] { "--" }, StringSplitOptions.None);

            for (int i = 0; i < newargs.Length; i++)
                newargs[i] = newargs[i].Trim();

            newargs = newargs.Where(a => !a.Equals("")).ToArray();
            if (newargs.Length == 0)
            {
                Console.WriteLine("Accepted args are: " + string.Join(" ", validArgs) + "\n");
                Console.ReadKey();
                return false;
            }

            foreach (var arg in newargs)
            {
                try
                {
                    string flag = arg.Substring(0, 3);

                    switch (flag)
                    {
                        case ("idt"): identificationsFilePath = arg.Substring(3); break;
                        case ("raw"): filePaths = new string[] { arg.Substring(3) }; break;
                        case ("rep"):
                            string newArg = arg;
                            if (newArg.EndsWith("\""))
                                newArg = arg.Substring(0, arg.Length - 1);
                            filePaths = Directory.GetFiles(newArg.Substring(3)).Where(f => f.Substring(f.IndexOf('.')).ToUpper().Equals(".RAW") || f.Substring(f.IndexOf('.')).ToUpper().Equals(".MZML")).ToArray(); break;
                        case ("ppm"): ppmTolerance = double.Parse(arg.Substring(3)); break;
                        case ("ret"): rtTolerance = double.Parse(arg.Substring(3)); break;
                        case ("iso"): isotopePpmTolerance = double.Parse(arg.Substring(3)); break;
                        case ("sil"): silent = Boolean.Parse(arg.Substring(3)); break;
                        case ("pau"): pause = Boolean.Parse(arg.Substring(3)); break;
                        case ("int"): integrate = Boolean.Parse(arg.Substring(3)); break;
                        case ("sum"): sumFeatures = Boolean.Parse(arg.Substring(3)); break;
                        default:
                            if (!silent)
                            {
                                Console.WriteLine("Not a known argument: \"" + flag + "\"\n");
                                Console.WriteLine("Accepted args are: " + string.Join(" ", validArgs) + "\n");
                                Console.WriteLine("Press any key to exit");
                                Console.ReadKey();
                            }
                            return false;
                    }
                }
                catch (Exception)
                {
                    if (!silent)
                    {
                        Console.WriteLine("Can't parse argument \"" + arg + "\"\n");
                        Console.WriteLine("Accepted args are: " + string.Join(" ", validArgs) + "\n");
                        Console.WriteLine("Press any key to exit");
                        Console.ReadKey();
                    }
                    return false;
                }
            }

            if (filePaths.Length == 0)
            {
                if (!silent)
                {
                    Console.WriteLine("Couldn't find any MS data files at the location specified\n");
                    Console.WriteLine("Press any key to exit");
                    Console.ReadKey();
                }
                return false;
            }

            return true;
        }

        public bool ReadPeriodicTable()
        {
            try
            {
                Loaders.LoadElements(@"elements.dat");
            }
            catch (Exception)
            {
                if (!silent)
                {
                    Console.WriteLine("\nCan't read periodic table file\n");
                    Console.WriteLine("Press any key to exit");
                    Console.ReadKey();
                }
                return false;
            }
            return true;
        }

        public bool ReadIdentificationsFromTSV()
        {
            // read identification file
            if (!silent)
                Console.WriteLine("Opening " + identificationsFilePath);
            string[] tsv;

            try
            {
                tsv = File.ReadAllLines(identificationsFilePath);
            }
            catch (FileNotFoundException)
            {
                if (!silent)
                {
                    Console.WriteLine("\nCan't find identification file\n");
                    Console.WriteLine("Press any key to exit");
                    Console.ReadKey();
                }
                return false;
            }
            catch (FileLoadException)
            {
                if (!silent)
                {
                    Console.WriteLine("\nCan't read identification file\n");
                    Console.WriteLine("Press any key to exit");
                    Console.ReadKey();
                }
                return false;
            }
            catch (Exception e)
            {
                if (!silent)
                {
                    Console.WriteLine("\nError reading identification file\n");
                    Console.WriteLine(e.Message + "\nPress any key to exit");
                    Console.ReadKey();
                }
                return false;
            }

            int lineCount = 0;
            foreach (var line in tsv)
            {
                lineCount++;
                var delimiters = new char[] { '\t' };

                if (lineCount == 1)
                    featureOutputHeader = line.Split(delimiters);
                else
                {
                    try
                    {
                        allIdentifications.Add(new Identification(line.Split(delimiters)));
                    }
                    catch (Exception)
                    {
                        if (!silent)
                        {
                            Console.WriteLine("Problem reading line " + lineCount + " of the TSV file");
                            Console.ReadKey();
                        }
                        return false;
                    }
                }
            }

            return true;
        }

        public void Quantify(int fileIndex)
        {
            // construct bins
            var bins = ConstructBinsFromIdentifications();

            // open raw file
            var currentDataFile = ReadMSFile(fileIndex);
            if (currentDataFile == null)
                return;

            // fill bins with peaks from the raw file
            var ms1ScanNumbers = FillBinsWithPeaks(bins, currentDataFile);

            // quantify features using this file's IDs first, then run the MBR functions
            var thisFilename = Path.GetFileNameWithoutExtension(filePaths[fileIndex]);
            
            MainFileSearch(thisFilename, bins, ms1ScanNumbers);

            // find unassigned features based on other files' identification results (MBR)
            //var identificationsFromOtherFiles = groups.Where(p => !p.Key.Equals(thisFilename)).SelectMany(p => p);
            //if (identificationsFromOtherFiles.Any() && mbr)
            //    MatchBetweenRuns(fileIndex);

            // error checking function
            // handles features with multiple identifying scans, and
            // also handles scans that are associated with more than one feature
            ErrorCheckingSingleFile();

            foreach(var feature in allFeatures)
                foreach (var cluster in feature.isotopeClusters)
                    cluster.peakWithScan.Compress();
        }
        
        public bool WriteResults()
        {
            if (!silent)
                Console.WriteLine("Writing results");

            string identificationFilePathNoExtention = identificationsFilePath.Substring(0, identificationsFilePath.Length - (identificationsFilePath.Length - identificationsFilePath.IndexOf('.')));

            try
            {
                // print features
                featureOutputHeader = new string[] { "feature header test" };
                List<string> featureOutput = new List<string> { string.Join("\t", featureOutputHeader) };
                featureOutput = featureOutput.Concat(allFeatures.Select(p => p.ToString())).ToList();
                File.WriteAllLines(identificationFilePathNoExtention + "_FlashQuant_Features.tsv", featureOutput);

                // print baseseq groups
                var baseSeqOutputHeader = new string[1 + filePaths.Length];
                baseSeqOutputHeader[0] = "BaseSequence";
                for (int i = 1; i < baseSeqOutputHeader.Length; i++)
                    baseSeqOutputHeader[i] = Path.GetFileName(filePaths[i - 1]);
                List<string> baseSeqOutput = new List<string> { string.Join("\t", baseSeqOutputHeader) };
                baseSeqOutput = baseSeqOutput.Concat(SumFeatures(allFeatures).Select(p => p.ToString())).ToList();
                File.WriteAllLines(identificationFilePathNoExtention + "_FlashQuant_BaseSeqGroups.tsv", baseSeqOutput);
            }
            catch (Exception)
            {
                if (!silent)
                {
                    Console.WriteLine("Unable to write results file for " + identificationFilePathNoExtention);
                    Console.WriteLine("Press any key to continue\n");
                    Console.ReadKey();
                }
                return false;
            }

            return true;
        }

        private IMsDataFile<IMsDataScan<IMzSpectrum<IMzPeak>>> ReadMSFile(int fileIndex)
        {
            var massSpecFileFormat = filePaths[fileIndex].Substring(filePaths[fileIndex].IndexOf('.')).ToUpper();
            IMsDataFile<IMsDataScan<IMzSpectrum<IMzPeak>>> file = null;

            // read mass spec file
            if (!silent)
                Console.WriteLine("Opening " + filePaths[fileIndex]);
            if (massSpecFileFormat == ".RAW")
            {
                try
                {
                    file = ThermoDynamicData.InitiateDynamicConnection(filePaths[fileIndex]);
                }
                catch (Exception)
                {
                    if (!silent)
                    {
                        Console.WriteLine("Problem opening raw file " + filePaths[fileIndex] + "\nPress any key to exit");
                        Console.ReadKey();
                    }
                    return file;
                }
            }
            else if (massSpecFileFormat == ".MZML")
            {
                try
                {
                    file = Mzml.LoadAllStaticData(filePaths[fileIndex]);
                }
                catch (Exception)
                {
                    if (!silent)
                    {
                        Console.WriteLine("Problem opening mzML file " + filePaths[fileIndex] + "\nPress any key to exit");
                        Console.ReadKey();
                    }
                    return file;
                }
            }
            else
            {
                if (!silent)
                {
                    Console.WriteLine("\nUnsupported file format\nPress any key to exit");
                    Console.ReadKey();
                }
                return file;
            }

            // input is good
            return file;
        }

        private Dictionary<double, List<MzBinElement>> ConstructBinsFromIdentifications()
        {
            if (!silent)
                Console.WriteLine("Constructing m/z bins");

            var mzBins = new Dictionary<double, List<MzBinElement>>();

            var peptideGroups = allIdentifications.GroupBy(p => p.FullSequence).ToList();
            var peptideBaseSeqs = new HashSet<string>(allIdentifications.Select(p => p.BaseSequence));
            var numCarbonsToIsotopicDistribution = new Dictionary<int, IsotopicDistribution>();

            foreach (var baseSeq in peptideBaseSeqs)
            {
                Peptide p = new Peptide(baseSeq);
                int numCarbonsInThisPeptide = p.ElementCountWithIsotopes("C");

                // get expected C13 mass shifts and abundances
                IsotopicDistribution isotopicDistribution;
                if (!numCarbonsToIsotopicDistribution.TryGetValue(numCarbonsInThisPeptide, out isotopicDistribution))
                {
                    isotopicDistribution = IsotopicDistribution.GetDistribution(ChemicalFormula.ParseFormula("C" + numCarbonsInThisPeptide), 0.00001, 0.001);
                    numCarbonsToIsotopicDistribution.Add(numCarbonsInThisPeptide, isotopicDistribution);
                }

                var masses = isotopicDistribution.Masses.ToArray();
                var abundances = isotopicDistribution.Intensities.ToArray();
                var isotopicMassesAndNormalizedAbundances = new List<KeyValuePair<double, double>>();

                var monoisotopicMass = masses.Min();
                var highestAbundance = abundances.Max();

                for (int i = 0; i < masses.Length; i++)
                {
                    // expected isotopic mass shifts for peptide of this length
                    masses[i] -= monoisotopicMass;

                    // normalized abundance of each mass shift
                    abundances[i] /= highestAbundance;

                    // if normalized abundance is higher than 20%, check for this peak in the data
                    if (abundances[i] > 0.2)
                        isotopicMassesAndNormalizedAbundances.Add(new KeyValuePair<double, double>(masses[i], abundances[i]));
                }

                baseSequenceToIsotopicDistribution.Add(baseSeq, isotopicMassesAndNormalizedAbundances);
            }

            var minChargeState = allIdentifications.Select(p => p.initialChargeState).Min();
            var maxChargeState = allIdentifications.Select(p => p.initialChargeState).Max();
            chargeStates = Enumerable.Range(minChargeState, maxChargeState - 1);

            // build theoretical m/z bins
            foreach (var pepGroup in peptideGroups)
            {
                double lowestCommonMassShift = baseSequenceToIsotopicDistribution[pepGroup.First().BaseSequence].Select(p => p.Key).Min();
                var mostCommonIsotopeShift = baseSequenceToIsotopicDistribution[pepGroup.First().BaseSequence].Where(p => p.Value == 1).First().Key;

                var thisPeptidesLowestCommonMass = pepGroup.First().monoisotopicMass + lowestCommonMassShift;
                var thisPeptidesMostAbundantMass = pepGroup.First().monoisotopicMass + mostCommonIsotopeShift;

                foreach (var pep in pepGroup)
                {
                    //pep.massToLookFor = thisPeptidesMostAbundantMass;
                    //pep.massToLookFor = pepGroup.First().monoisotopicMass;
                    pep.massToLookFor = thisPeptidesLowestCommonMass;
                }

                foreach (var chargeState in chargeStates)
                {
                    var t = ClassExtensions.ToMz(pepGroup.First().massToLookFor, chargeState);
                    double floorMz = Math.Floor(t * 100) / 100;
                    double ceilingMz = Math.Ceiling(t * 100) / 100;

                    if (!mzBins.ContainsKey(floorMz))
                        mzBins.Add(floorMz, new List<MzBinElement>());
                    if (!mzBins.ContainsKey(ceilingMz))
                        mzBins.Add(ceilingMz, new List<MzBinElement>());
                }
            }

            return mzBins;
        }

        private List<int> FillBinsWithPeaks(Dictionary<double, List<MzBinElement>> mzBins, IMsDataFile<IMsDataScan<IMzSpectrum<IMzPeak>>> file)
        {
            var allMs1Scans = new List<IMsDataScan<IMzSpectrum<IMzPeak>>>();

            // thermo files read differently than mzml
            var thermoFile = file as ThermoDynamicData;
            if (thermoFile != null)
            {
                int[] msOrders = thermoFile.ThermoGlobalParams.msOrderByScan;
                for (int i = 0; i < msOrders.Length; i++)
                    if (msOrders[i] == 1)
                        allMs1Scans.Add(thermoFile.GetOneBasedScan(i + 1));
            }
            else
                allMs1Scans = file.Where(s => s.MsnOrder == 1).ToList();

            var ms1ScanNumbers = allMs1Scans.Select(p => p.OneBasedScanNumber).Distinct().OrderBy(p => p).ToList();

            if (!silent)
                Console.WriteLine("Assigning MS1 peaks to bins");

            /*
            Parallel.ForEach(Partitioner.Create(0, allMs1Scans.Count),
                // initialize thread-local mz bins
                () => mzBins.ToDictionary(x => x.Key, x => new List<MzBinElement>()),
                // process each ms1 scan block
                (scan, loopState, threadLocalDictionary) =>
                {
                    for (int i = scan.Item1; i < scan.Item2; i++)
                    {
                        int peakIndexInThisScan = 0;
                        double backgroundForThisScan = allMs1Scans[i].MassSpectrum.Select(p => p.Intensity).Min();

                        foreach (var peak in allMs1Scans[i].MassSpectrum)
                        {
                            double signalToBackgroundRatio = peak.Intensity / backgroundForThisScan;

                            if (signalToBackgroundRatio > signalToBackgroundRequired)
                            {
                                List<MzBinElement> mzBin;
                                MzBinElement element = new MzBinElement(peak, allMs1Scans[i], backgroundForThisScan, peakIndexInThisScan);
                                double floorMz = Math.Floor(peak.Mz * 100) / 100;
                                double ceilingMz = Math.Ceiling(peak.Mz * 100) / 100;

                                if (threadLocalDictionary.TryGetValue(floorMz, out mzBin))
                                    mzBin.Add(element);

                                if (threadLocalDictionary.TryGetValue(ceilingMz, out mzBin))
                                    mzBin.Add(element);
                            }

                            peakIndexInThisScan++;
                        }
                    }

                    return threadLocalDictionary;
                },
                // aggregate results from the individual threads
                threadLocalDictionary =>
                {
                    lock (mzBins)
                        foreach (var bin in mzBins.Keys)
                            mzBins[bin].AddRange(threadLocalDictionary[bin]);
                }
            );
            */

            foreach (var scan in allMs1Scans)
            {
                int peakIndexInThisScan = 0;
                double backgroundForThisScan = scan.MassSpectrum.Select(p => p.Intensity).Min();

                foreach (var peak in scan.MassSpectrum)
                {
                    double signalToBackgroundRatio = peak.Intensity / backgroundForThisScan;

                    if (signalToBackgroundRatio > signalToBackgroundRequired)
                    {
                        List<MzBinElement> mzBin;
                        MzBinElement element = null;
                        double floorMz = Math.Floor(peak.Mz * 100) / 100;
                        double ceilingMz = Math.Ceiling(peak.Mz * 100) / 100;

                        if (mzBins.TryGetValue(floorMz, out mzBin))
                        {
                            element = new MzBinElement(peak, scan, backgroundForThisScan, peakIndexInThisScan);
                            mzBin.Add(element);
                        }

                        if (mzBins.TryGetValue(ceilingMz, out mzBin))
                        {
                            if (element == null)
                                element = new MzBinElement(peak, scan, backgroundForThisScan, peakIndexInThisScan);
                            mzBin.Add(element);
                        }
                    }

                    peakIndexInThisScan++;
                }
            }

            return ms1ScanNumbers;
        }
        
        private void MainFileSearch(string fileName, Dictionary<double, List<MzBinElement>> mzBins, List<int> ms1ScanNumbers)
        {
            if (!silent)
                Console.WriteLine("Quantifying peptides");

            var groups = allIdentifications.GroupBy(p => p.fileName);
            var identificationsForThisFile = groups.Where(p => p.Key.Equals(fileName)).FirstOrDefault();
            if (identificationsForThisFile == null)
                return;

            var identifications = identificationsForThisFile.ToList();

            // stores quantification results from each thread (threadsafe data structure)
            ConcurrentBag<Feature> concurrentBagOfFeatures = new ConcurrentBag<Feature>();

            Parallel.ForEach(Partitioner.Create(0, identifications.Count), (range, loopState) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    var identification = identifications[i];
                    Feature msmsFeature = new Feature();
                    msmsFeature.identifyingScans.Add(identification);
                    msmsFeature.featureType = "MSMS";

                    foreach (var chargeState in chargeStates)
                    {
                        double theorMzHere = ClassExtensions.ToMz(identification.massToLookFor, chargeState);
                        double mzTolHere = (ppmTolerance / 1e6) * theorMzHere;

                        double floorMz = Math.Floor(theorMzHere * 100) / 100;
                        double ceilingMz = Math.Ceiling(theorMzHere * 100) / 100;

                        IEnumerable<MzBinElement> binPeaks = new List<MzBinElement>();
                        List<MzBinElement> t;
                        if (mzBins.TryGetValue(floorMz, out t))
                            binPeaks = binPeaks.Concat(t);
                        if (mzBins.TryGetValue(ceilingMz, out t))
                            binPeaks = binPeaks.Concat(t);

                        // filter by mz tolerance
                        var binPeaksHere = binPeaks.Where(p => Math.Abs(p.mainPeak.Mz - theorMzHere) < mzTolHere);
                        // filter by rt
                        binPeaksHere = binPeaksHere.Where(p => Math.Abs(p.retentionTime - identification.ms2RetentionTime) < rtTolerance);
                        // remove duplicates
                        binPeaksHere = binPeaksHere.Distinct();
                        
                        if (binPeaksHere.Any())
                        {
                            double bestRTDifference = binPeaksHere.Select(p => Math.Abs(p.retentionTime - identification.ms2RetentionTime)).Min();

                            if (bestRTDifference < initialRTWindow)
                            {
                                // get first good peak nearest to the identification's RT to look around
                                var bestPeakToStartAt = binPeaksHere.Where(p => bestRTDifference == Math.Abs(p.retentionTime - identification.ms2RetentionTime)).First();

                                // separate peaks by rt into left and right of the identification RT
                                var rightPeaks = binPeaksHere.Where(p => p.retentionTime >= bestPeakToStartAt.retentionTime).OrderBy(p => p.retentionTime);
                                var leftPeaks = binPeaksHere.Where(p => p.retentionTime < bestPeakToStartAt.retentionTime).OrderByDescending(p => p.retentionTime);

                                // store peaks on each side of the identification RT
                                var crawledRightPeaks = ScanCrawl(rightPeaks, missedScansAllowed, bestPeakToStartAt.oneBasedScanNumber, ms1ScanNumbers);
                                var crawledLeftPeaks = ScanCrawl(leftPeaks, missedScansAllowed, bestPeakToStartAt.oneBasedScanNumber, ms1ScanNumbers);

                                var validPeaks = crawledRightPeaks.Concat(crawledLeftPeaks);

                                validPeaks = FilterPeaksByIsotopicDistribution(validPeaks, identification, chargeState);

                                foreach (var validPeak in validPeaks)
                                    msmsFeature.isotopeClusters.Add(new IsotopeCluster(validPeak, chargeState));
                            }
                        }
                    }

                    msmsFeature.CalculateIntensityForThisFeature(fileName, integrate);

                    

                    concurrentBagOfFeatures.Add(msmsFeature);
                }
            });

            // merge results from all threads together
            allFeatures.AddRange(concurrentBagOfFeatures);
        }
        
        /*
        private void MatchBetweenRuns(int thisRawFilesIndex)
        {
            var thisFilename = Path.GetFileNameWithoutExtension(massSpecFilePaths[thisRawFilesIndex]);
            var identificationsFromOtherRunsToLookFor = new List<Identification>();
            var idsGroupedByFullSeq = allIdentifications.GroupBy(p => p.FullSequence);

            foreach (var group in idsGroupedByFullSeq)
            {
                // look for peptides with no ID's in this file
                var seqsByFilename = group.GroupBy(p => p.fileName);

                if (!seqsByFilename.Where(p => p.Key.Equals(thisFilename)).Any())
                    identificationsFromOtherRunsToLookFor = identificationsFromOtherRunsToLookFor.Union(group).ToList();

                // also look for features that were possibly missed even though the peptide was observed in this run
            }

            foreach (Identification identification in identificationsFromOtherRunsToLookFor)
            {
                Feature mbrFeature = new Feature();
                mbrFeature.identifyingScans.Add(identification);
                mbrFeature.featureType = "MBR";
                mbrFeature.fileName = thisFilename;

                foreach (var chargeState in chargeStates)
                {
                    double theorMzHere = ClassExtensions.ToMz(identification.massToLookFor, chargeState);
                    double mzTolHere = (ppmTolerance / 1e6) * theorMzHere;

                    double floorMz = Math.Floor(theorMzHere * 100) / 100;
                    double ceilingMz = Math.Ceiling(theorMzHere * 100) / 100;

                    IEnumerable<MzBinElement> binPeaks = new List<MzBinElement>();
                    List<MzBinElement> t;
                    if (mzBins.TryGetValue(floorMz, out t))
                        binPeaks = binPeaks.Concat(t);
                    if (mzBins.TryGetValue(ceilingMz, out t))
                        binPeaks = binPeaks.Concat(t);

                    // filter by mz tolerance
                    var binPeaksHere = binPeaks.Where(p => Math.Abs(p.mainPeak.Mz - theorMzHere) < mzTolHere);
                    // filter by rt
                    binPeaksHere = binPeaksHere.Where(p => Math.Abs(p.retentionTime - identification.ms2RetentionTime) < mbrRtWindow);
                    // filter by signal to background ratio
                    //binPeaksHere = binPeaksHere.Where(p => p.signalToBackgroundRatio > mbrSignalToBackgroundRequired);
                    // remove duplicates
                    binPeaksHere = binPeaksHere.Distinct();
                    // filter by isotopic distribution
                    binPeaksHere = FilterPeaksByIsotopicDistribution(binPeaksHere, identification, chargeState);

                    if (binPeaksHere.Any())
                    {
                        double apexIntensity = binPeaksHere.Select(p => p.backgroundSubtractedIntensity).Max();
                        var mbrApexPeak = binPeaksHere.Where(p => p.backgroundSubtractedIntensity == apexIntensity).First();

                        mbrFeature.isotopeClusters.Add(new IsotopeCluster(mbrApexPeak, chargeState));
                    }
                }

                if (mbrFeature.isotopeClusters.Any())
                {
                    mbrFeature.CalculateIntensityForThisFeature(thisRawFilesIndex, integrate);
                    allFeatures.Add(mbrFeature);
                }
            }
        }
        */

        private void ErrorCheckingSingleFile()
        {
            var featuresWithSamePeak = allFeatures.Where(v => v.intensity != 0).GroupBy(p => p.apexPeak.peakWithScan);
            featuresWithSamePeak = featuresWithSamePeak.Where(p => p.Count() > 1);

            // condense features that have been assigned to the same peptide twice
            foreach (var duplicateFeature in featuresWithSamePeak)
            {
                //int numFullSeqs = duplicateFeature.SelectMany(p => p.identifyingScans.Select(v => v.FullSequence)).Distinct().Count();
                //if(numFullSeqs == 1)
                duplicateFeature.First().MergeFeatureWith(duplicateFeature);
            }

            allFeatures.RemoveAll(p => p.intensity == -1);

            // check for multiple features per peptide
            // check for multiple peptides per feature
        }

        private List<SummedFeatureGroup> SumFeatures(List<Feature> features)
        {
            List<SummedFeatureGroup> returnList = new List<SummedFeatureGroup>();

            var baseSeqToFeatureMatch = new Dictionary<string, List<Feature>>();
            foreach (var feature in features)
            {
                foreach (var id in feature.identifyingScans)
                {
                    List<Feature> featuresForThisBaseSeq;
                    if (baseSeqToFeatureMatch.TryGetValue(id.BaseSequence, out featuresForThisBaseSeq))
                        featuresForThisBaseSeq.Add(feature);
                    else
                        baseSeqToFeatureMatch.Add(id.BaseSequence, new List<Feature>() { feature });
                }
            }

            foreach (var sequence in baseSeqToFeatureMatch)
            {
                double[] intensitiesByFile = new double[filePaths.Length];
                var thisSeqPerFile = sequence.Value.GroupBy(p => p.fileName);

                for (int i = 0; i < intensitiesByFile.Length; i++)
                {
                    string file = Path.GetFileNameWithoutExtension(filePaths[i]);
                    var featuresForThisBaseSeqAndFile = thisSeqPerFile.Where(p => p.Key.Equals(file)).FirstOrDefault();

                    if (featuresForThisBaseSeqAndFile != null)
                        intensitiesByFile[i] = featuresForThisBaseSeqAndFile.Select(p => p.intensity).Sum();
                }

                returnList.Add(new SummedFeatureGroup(sequence.Key, intensitiesByFile));
            }

            return returnList;
        }

        private IEnumerable<MzBinElement> FilterPeaksByIsotopicDistribution(IEnumerable<MzBinElement> peaks, Identification identification, int chargeState)
        {
            var goodPeaks = new List<MzBinElement>();

            foreach (var thisPeakWithScan in peaks)
            {
                var isotopeMassShifts = baseSequenceToIsotopicDistribution[identification.BaseSequence];
                var isotopes = isotopeMassShifts.Select(p => new KeyValuePair<double, double>(p.Key + ClassExtensions.ToMass(thisPeakWithScan.mainPeak.Mz, chargeState), p.Value)).ToList();
                if (isotopes.Count < numberOfIsotopesToLookFor)
                    numberOfIsotopesToLookFor = isotopes.Count;
                IMzPeak[] isotopePeaks = new IMzPeak[numberOfIsotopesToLookFor];

                var lowestMzIsotopePossible = ClassExtensions.ToMz(isotopes[0].Key, chargeState);
                lowestMzIsotopePossible -= (ppmTolerance / 1e6) * lowestMzIsotopePossible;
                var highestMzIsotopePossible = ClassExtensions.ToMz(isotopes[numberOfIsotopesToLookFor - 1].Key, chargeState);
                highestMzIsotopePossible += (ppmTolerance / 1e6) * highestMzIsotopePossible;

                bool isotopeDistributionCheck = false;

                // get possible isotope peaks from the peak's scan
                List<IMzPeak> possibleIsotopePeaks = new List<IMzPeak>();

                for (int i = thisPeakWithScan.zeroBasedIndexOfPeakInScan; i < thisPeakWithScan.scan.MassSpectrum.Size; i++)
                {
                    if (thisPeakWithScan.scan.MassSpectrum[i].Mz > highestMzIsotopePossible)
                        break;
                    possibleIsotopePeaks.Add(thisPeakWithScan.scan.MassSpectrum[i]);
                }

                // order theoretical isotope peaks by expected m/z (ascending)
                isotopes = isotopes.OrderBy(p => p.Key).ToList();

                int isotopeIndex = 0;
                double theorIsotopeMz = ClassExtensions.ToMz(isotopes[isotopeIndex].Key, chargeState);
                double isotopeMzTol = ((isotopePpmTolerance / 1e6) * isotopes[isotopeIndex].Key) / chargeState;

                foreach (var possibleIsotopePeak in possibleIsotopePeaks)
                {
                    if (Math.Abs(possibleIsotopePeak.Mz - theorIsotopeMz) < isotopeMzTol)
                    {
                        // store the good isotope peak
                        isotopePeaks[isotopeIndex] = possibleIsotopePeak;

                        if (isotopeIndex < numberOfIsotopesToLookFor - 1)
                        {
                            // look for the next isotope
                            isotopeIndex++;
                            theorIsotopeMz = ClassExtensions.ToMz(isotopes[isotopeIndex].Key, chargeState);
                            isotopeMzTol = ((isotopePpmTolerance / 1e6) * isotopes[isotopeIndex].Key) / chargeState;
                        }
                        else
                        {
                            // all isotopes have been looked for - check to see if they've been observed
                            if (!isotopePeaks.Where(p => p == null).Any())
                                isotopeDistributionCheck = true;
                        }
                    }
                }

                if (isotopeDistributionCheck)
                    goodPeaks.Add(thisPeakWithScan);
            }

            return goodPeaks;
        }

        private IEnumerable<MzBinElement> ScanCrawl(IOrderedEnumerable<MzBinElement> peaksWithScans, int missedScansAllowed, int startingMS1ScanNumber, List<int> ms1ScanNumbers)
        {
            var validPeaksWithScans = new List<MzBinElement>();

            int lastGoodIndex = ms1ScanNumbers.IndexOf(startingMS1ScanNumber);
            int ms1IndexHere = lastGoodIndex - 1;
            int missedScans = 0;

            foreach (var thisPeakWithScan in peaksWithScans)
            {
                ms1IndexHere = ms1ScanNumbers.IndexOf(thisPeakWithScan.oneBasedScanNumber);
                missedScans += Math.Abs((ms1IndexHere - lastGoodIndex)) - 1;

                if (missedScans > missedScansAllowed)
                    break;

                // found a good peak; reset missed scans to 0
                missedScans = 0;
                lastGoodIndex = ms1IndexHere;

                validPeaksWithScans.Add(thisPeakWithScan);
            }

            return validPeaksWithScans;
        }

        private void MbrRtCalibrationAndErrorChecking()
        {

        }
    }
}