using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Machine Learning-based pattern prediction for brain wallet recovery
    /// Uses statistical analysis and learned patterns from known compromised wallets
    /// </summary>
    public class MLPatternPredictor
    {
        private readonly List<PatternModel> _patternModels;
        private readonly Dictionary<string, double> _wordFrequencies;
        private readonly Dictionary<string, double> _bigramFrequencies;
        private readonly Dictionary<string, double> _trigramFrequencies;
        private readonly HashSet<string> _knownCompromisedPhrases;
        private readonly Random _random = new Random();

        public event EventHandler<PredictionEventArgs> PredictionGenerated;
        public event EventHandler<TrainingProgressEventArgs> TrainingProgress;

        public MLPatternPredictor()
        {
            _patternModels = new List<PatternModel>();
            _wordFrequencies = new Dictionary<string, double>();
            _bigramFrequencies = new Dictionary<string, double>();
            _trigramFrequencies = new Dictionary<string, double>();
            _knownCompromisedPhrases = new HashSet<string>();
            
            InitializeBaseModels();
            LoadKnownCompromisedPatterns();
        }

        #region Pattern Models

        private class PatternModel
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public Func<string> Generator { get; set; }
            public double Weight { get; set; } = 1.0;
            public int SuccessCount { get; set; }
        }

        private void InitializeBaseModels()
        {
            _patternModels.AddRange(new[]
            {
                // Keyboard patterns
                new PatternModel
                {
                    Name = "KeyboardRow",
                    Description = "Keyboard row patterns (qwerty, asdf, etc.)",
                    Generator = GenerateKeyboardPattern,
                    Weight = 3.0
                },
                
                // Number sequences
                new PatternModel
                {
                    Name = "NumberSequence",
                    Description = "Sequential numbers (123456, 111111, etc.)",
                    Generator = GenerateNumberSequence,
                    Weight = 4.0
                },
                
                // Date patterns
                new PatternModel
                {
                    Name = "DatePattern",
                    Description = "Dates (birthdays, anniversaries)",
                    Generator = GenerateDatePattern,
                    Weight = 3.5
                },
                
                // Common words combinations
                new PatternModel
                {
                    Name = "CommonWordCombo",
                    Description = "Common word combinations",
                    Generator = GenerateCommonWordCombo,
                    Weight = 2.5
                },
                
                // Movie/Music references
                new PatternModel
                {
                    Name = "PopCultureRef",
                    Description = "Pop culture references",
                    Generator = GeneratePopCultureReference,
                    Weight = 2.0
                },
                
                // Religious/Spiritual
                new PatternModel
                {
                    Name = "Religious",
                    Description = "Religious phrases",
                    Generator = GenerateReligiousPhrase,
                    Weight = 2.0
                },
                
                // Sports teams/players
                new PatternModel
                {
                    Name = "SportsRef",
                    Description = "Sports references",
                    Generator = GenerateSportsReference,
                    Weight = 1.5
                },
                
                // Leetspeak variations
                new PatternModel
                {
                    Name = "Leetspeak",
                    Description = "Leetspeak variations",
                    Generator = GenerateLeetspeak,
                    Weight = 1.5
                },
                
                // Personal info patterns
                new PatternModel
                {
                    Name = "PersonalInfo",
                    Description = "Name + year patterns",
                    Generator = GeneratePersonalInfo,
                    Weight = 3.0
                },
                
                // Crypto-specific
                new PatternModel
                {
                    Name = "CryptoTerms",
                    Description = "Cryptocurrency slang",
                    Generator = GenerateCryptoTerm,
                    Weight = 2.5
                }
            });
        }

        #endregion

        #region Pattern Generators

        private static readonly string[] KeyboardRows = 
        {
            "qwertyuiop", "asdfghjkl", "zxcvbnm",
            "1234567890", "!@#$%^&*()"
        };

        private string GenerateKeyboardPattern()
        {
            var row = KeyboardRows[_random.Next(KeyboardRows.Length)];
            var startIndex = _random.Next(row.Length - 3);
            var length = _random.Next(4, Math.Min(10, row.Length - startIndex + 1));
            var pattern = row.Substring(startIndex, length);
            
            // Variations
            return _random.Next(4) switch
            {
                0 => pattern,
                1 => pattern + pattern, // Repeat
                2 => pattern.ToUpper(),
                _ => ReverseString(pattern)
            };
        }

        private string GenerateNumberSequence()
        {
            return _random.Next(10) switch
            {
                0 => string.Concat(Enumerable.Repeat(_random.Next(10).ToString(), _random.Next(4, 10))), // 111111
                1 => string.Join("", Enumerable.Range(1, _random.Next(6, 12))), // 123456789
                2 => string.Join("", Enumerable.Range(0, _random.Next(6, 10)).Select(i => (i * 2) % 10)), // 0246802468
                3 => string.Join("", Enumerable.Range(1, _random.Next(6, 10)).Reverse()), // 987654321
                4 => $"{_random.Next(1, 13):D2}{_random.Next(1, 32):D2}{_random.Next(1950, 2025)}", // MMDDYYYY
                5 => $"19{_random.Next(50, 100)}", // Years
                6 => $"20{_random.Next(0, 30):D2}",
                7 => $"{_random.Next(1, 1000000):D6}", // Random 6-digit
                8 => "0" + string.Concat(Enumerable.Range(0, 9).Select(i => i.ToString())), // 0123456789
                _ => $"{_random.Next(100, 1000)}{_random.Next(100, 1000)}" // XXX-XXX
            };
        }

        private string GenerateDatePattern()
        {
            var year = _random.Next(1960, 2010);
            var month = _random.Next(1, 13);
            var day = _random.Next(1, 29);
            
            return _random.Next(8) switch
            {
                0 => $"{month:D2}{day:D2}{year}",
                1 => $"{day:D2}{month:D2}{year}",
                2 => $"{year}{month:D2}{day:D2}",
                3 => $"{month}/{day}/{year}",
                4 => $"{day}-{month}-{year}",
                5 => $"{month:D2}{day:D2}{year % 100:D2}",
                6 => $"{year}{month:D2}",
                _ => $"{day:D2}{month:D2}"
            };
        }

        private static readonly string[] CommonWords =
        {
            "password", "bitcoin", "crypto", "wallet", "money", "love", "baby", "angel",
            "dragon", "master", "sunshine", "princess", "monkey", "shadow", "tiger",
            "football", "soccer", "baseball", "hockey", "letmein", "welcome", "hello",
            "iloveyou", "trustno1", "diamond", "freedom", "superman", "batman", "qwerty"
        };

        private static readonly string[] Modifiers =
        {
            "123", "1", "!", "123!", "2023", "2024", "007", "1234", "69", "420", "666", "777"
        };

        private string GenerateCommonWordCombo()
        {
            var word = CommonWords[_random.Next(CommonWords.Length)];
            var modifier = _random.Next(3) == 0 ? "" : Modifiers[_random.Next(Modifiers.Length)];
            
            return _random.Next(4) switch
            {
                0 => word + modifier,
                1 => modifier + word,
                2 => word.ToUpper() + modifier,
                _ => char.ToUpper(word[0]) + word[1..] + modifier
            };
        }

        private static readonly string[] PopCultureRefs =
        {
            "starwars", "lordoftherings", "harrypotter", "gameofthrones", "avengers",
            "ironman", "spiderman", "thanos", "vader", "yoda", "frodo", "gandalf",
            "dumbledore", "voldemort", "pikachu", "pokemon", "minecraft", "fortnite",
            "rickandmorty", "breakingbad", "strangerthings", "theoffice", "friends"
        };

        private string GeneratePopCultureReference()
        {
            var reference = PopCultureRefs[_random.Next(PopCultureRefs.Length)];
            return _random.Next(3) switch
            {
                0 => reference,
                1 => reference + _random.Next(100, 1000).ToString(),
                _ => reference.ToUpper()
            };
        }

        private static readonly string[] ReligiousPhrases =
        {
            "godisgood", "godislove", "jesuslovesyou", "amen", "praise", "blessed",
            "faith", "hope", "charity", "john316", "psalm23", "proverbs", "genesis",
            "allah", "muhammad", "buddhist", "karma", "namaste", "shalom", "hallelujah"
        };

        private string GenerateReligiousPhrase()
        {
            return ReligiousPhrases[_random.Next(ReligiousPhrases.Length)];
        }

        private static readonly string[] SportsRefs =
        {
            "realmadrid", "barcelona", "manchester", "liverpool", "chelsea", "arsenal",
            "juventus", "bayern", "lakers", "celtics", "bulls", "yankees", "dodgers",
            "patriots", "cowboys", "packers", "jordan23", "lebron", "messi", "ronaldo"
        };

        private string GenerateSportsReference()
        {
            var reference = SportsRefs[_random.Next(SportsRefs.Length)];
            return _random.Next(2) == 0 ? reference : reference + _random.Next(1, 100).ToString();
        }

        private string GenerateLeetspeak()
        {
            var word = CommonWords[_random.Next(CommonWords.Length)];
            var leetMap = new Dictionary<char, char>
            {
                {'a', '4'}, {'e', '3'}, {'i', '1'}, {'o', '0'}, {'s', '5'},
                {'t', '7'}, {'b', '8'}, {'g', '9'}, {'l', '1'}
            };
            
            var result = new StringBuilder();
            foreach (var c in word.ToLower())
            {
                if (leetMap.TryGetValue(c, out var leet) && _random.Next(2) == 0)
                    result.Append(leet);
                else
                    result.Append(c);
            }
            
            return result.ToString();
        }

        private static readonly string[] CommonNames =
        {
            "michael", "james", "john", "david", "william", "richard", "joseph", "thomas",
            "mary", "jennifer", "elizabeth", "jessica", "ashley", "sarah", "amanda", "emily"
        };

        private string GeneratePersonalInfo()
        {
            var name = CommonNames[_random.Next(CommonNames.Length)];
            var year = _random.Next(1960, 2010);
            
            return _random.Next(6) switch
            {
                0 => name + year,
                1 => name + (year % 100).ToString("D2"),
                2 => char.ToUpper(name[0]) + name[1..] + year,
                3 => name + "123",
                4 => name + _random.Next(1, 32).ToString(),
                _ => name + name
            };
        }

        private static readonly string[] CryptoTerms =
        {
            "hodl", "tothemoon", "satoshi", "nakamoto", "blockchain", "defi", "nft",
            "diamondhands", "paperhands", "ape", "doge", "lambo", "wagmi", "ngmi",
            "gwei", "wei", "fomo", "fud", "rekt", "moonshot", "bullish", "bearish"
        };

        private string GenerateCryptoTerm()
        {
            var term = CryptoTerms[_random.Next(CryptoTerms.Length)];
            return _random.Next(4) switch
            {
                0 => term,
                1 => term + "2024",
                2 => term.ToUpper(),
                _ => term + "btc"
            };
        }

        private string ReverseString(string s)
        {
            return new string(s.Reverse().ToArray());
        }

        #endregion

        #region Known Compromised Patterns

        private void LoadKnownCompromisedPatterns()
        {
            // These are patterns from historically compromised brain wallets
            _knownCompromisedPhrases.UnionWith(new[]
            {
                "password", "123456", "12345678", "qwerty", "abc123", "monkey", "1234567",
                "letmein", "trustno1", "dragon", "baseball", "iloveyou", "master", "sunshine",
                "ashley", "bailey", "shadow", "123123", "654321", "superman", "qazwsx",
                "michael", "football", "password1", "password123", "welcome", "jesus", "ninja",
                "mustang", "password2", "password3", "whatever", "qwerty123", "passw0rd",
                "zxcvbnm", "aaaaaa", "121212", "fuck", "fuckyou", "fuckoff", "asshole",
                "bitcoin", "satoshi", "blockchain", "money", "cash", "rich", "millionaire"
            });
        }

        /// <summary>
        /// Trains the predictor with known compromised phrases
        /// </summary>
        public void TrainFromDataset(IEnumerable<string> compromisedPhrases)
        {
            int total = compromisedPhrases.Count();
            int current = 0;

            foreach (var phrase in compromisedPhrases)
            {
                current++;
                _knownCompromisedPhrases.Add(phrase.ToLower());
                
                // Build n-gram frequencies
                UpdateFrequencies(phrase.ToLower());
                
                if (current % 1000 == 0)
                {
                    TrainingProgress?.Invoke(this, new TrainingProgressEventArgs
                    {
                        ProcessedItems = current,
                        TotalItems = total,
                        Message = $"Processed {current}/{total} phrases"
                    });
                }
            }

            // Normalize frequencies
            NormalizeFrequencies();
        }

        private void UpdateFrequencies(string phrase)
        {
            // Word frequency
            foreach (var word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                _wordFrequencies.TryGetValue(word, out var count);
                _wordFrequencies[word] = count + 1;
            }

            // Bigram frequency
            for (int i = 0; i < phrase.Length - 1; i++)
            {
                var bigram = phrase.Substring(i, 2);
                _bigramFrequencies.TryGetValue(bigram, out var count);
                _bigramFrequencies[bigram] = count + 1;
            }

            // Trigram frequency
            for (int i = 0; i < phrase.Length - 2; i++)
            {
                var trigram = phrase.Substring(i, 3);
                _trigramFrequencies.TryGetValue(trigram, out var count);
                _trigramFrequencies[trigram] = count + 1;
            }
        }

        private void NormalizeFrequencies()
        {
            if (_wordFrequencies.Any())
            {
                var maxWord = _wordFrequencies.Values.Max();
                foreach (var key in _wordFrequencies.Keys.ToList())
                    _wordFrequencies[key] /= maxWord;
            }

            if (_bigramFrequencies.Any())
            {
                var maxBigram = _bigramFrequencies.Values.Max();
                foreach (var key in _bigramFrequencies.Keys.ToList())
                    _bigramFrequencies[key] /= maxBigram;
            }
        }

        #endregion

        #region Prediction Generation

        /// <summary>
        /// Generates predicted brain wallet phrases based on learned patterns
        /// </summary>
        public IEnumerable<PredictedPhrase> GeneratePredictions(int count = 1000)
        {
            var predictions = new List<PredictedPhrase>();
            var seen = new HashSet<string>();

            // First, yield all known compromised phrases that haven't been scanned empty
            foreach (var known in _knownCompromisedPhrases.Take(count / 4))
            {
                if (!GlobalMnemonicTracker.Instance.WasScanned($"BRAIN:{known}") && seen.Add(known))
                {
                    var pred = new PredictedPhrase
                    {
                        Phrase = known,
                        Confidence = 1.0,
                        PatternType = "Known Compromised"
                    };
                    predictions.Add(pred);
                    PredictionGenerated?.Invoke(this, new PredictionEventArgs { Prediction = pred });
                }
            }

            // Generate from pattern models
            int remaining = count - predictions.Count;
            double totalWeight = _patternModels.Sum(m => m.Weight);
            
            foreach (var model in _patternModels)
            {
                int modelCount = (int)(remaining * model.Weight / totalWeight);
                
                for (int i = 0; i < modelCount && predictions.Count < count; i++)
                {
                    try
                    {
                        var phrase = model.Generator();
                        if (!string.IsNullOrEmpty(phrase) &&
                            !GlobalMnemonicTracker.Instance.WasScanned($"BRAIN:{phrase}") &&
                            seen.Add(phrase.ToLower()))
                        {
                            var confidence = CalculateConfidence(phrase, model);
                            var pred = new PredictedPhrase
                            {
                                Phrase = phrase,
                                Confidence = confidence,
                                PatternType = model.Name
                            };
                            predictions.Add(pred);
                            PredictionGenerated?.Invoke(this, new PredictionEventArgs { Prediction = pred });
                        }
                    }
                    catch { }
                }
            }

            // Generate variations of high-confidence predictions
            var highConfidence = predictions.Where(p => p.Confidence > 0.7).ToList();
            foreach (var pred in highConfidence)
            {
                if (predictions.Count >= count) break;
                
                foreach (var variation in GenerateVariations(pred.Phrase))
                {
                    if (seen.Add(variation.ToLower()) && predictions.Count < count)
                    {
                        var newPred = new PredictedPhrase
                        {
                            Phrase = variation,
                            Confidence = pred.Confidence * 0.8,
                            PatternType = pred.PatternType + " (Variation)"
                        };
                        predictions.Add(newPred);
                    }
                }
            }

            return predictions.OrderByDescending(p => p.Confidence);
        }

        private double CalculateConfidence(string phrase, PatternModel model)
        {
            double confidence = 0.3; // Base confidence
            
            // Boost for model weight
            confidence += model.Weight / 10;
            
            // Boost for known patterns
            if (_knownCompromisedPhrases.Contains(phrase.ToLower()))
                confidence += 0.5;
            
            // Boost for common character patterns
            if (phrase.All(char.IsLetter))
                confidence += 0.05;
            if (phrase.All(char.IsDigit))
                confidence += 0.1;
            
            // Boost for common lengths
            if (phrase.Length >= 6 && phrase.Length <= 12)
                confidence += 0.1;
            
            // N-gram scoring
            var bigramScore = phrase.Length > 1 
                ? Enumerable.Range(0, phrase.Length - 1)
                    .Select(i => phrase.Substring(i, 2))
                    .Average(bg => _bigramFrequencies.GetValueOrDefault(bg, 0))
                : 0;
            confidence += bigramScore * 0.2;

            return Math.Min(1.0, confidence);
        }

        private IEnumerable<string> GenerateVariations(string phrase)
        {
            var variations = new List<string>
            {
                phrase + "1",
                phrase + "123",
                phrase + "!",
                phrase + "2024",
                phrase.ToUpper(),
                phrase.ToLower(),
                char.ToUpper(phrase[0]) + phrase[1..],
                phrase + phrase,
                ReverseString(phrase)
            };

            // Leetspeak variation
            var leetMap = new Dictionary<char, char>
            {
                {'a', '4'}, {'e', '3'}, {'i', '1'}, {'o', '0'}, {'s', '5'},
                {'t', '7'}, {'b', '8'}, {'g', '9'}
            };
            
            var leet = new StringBuilder();
            foreach (var c in phrase.ToLower())
            {
                leet.Append(leetMap.GetValueOrDefault(c, c));
            }
            variations.Add(leet.ToString());

            return variations.Where(v => v != phrase);
        }

        #endregion

        #region Markov Chain Generation

        private readonly Dictionary<string, List<char>> _markovChain = new();

        /// <summary>
        /// Builds Markov chain from training data
        /// </summary>
        public void BuildMarkovChain(IEnumerable<string> phrases, int order = 2)
        {
            foreach (var phrase in phrases)
            {
                var padded = new string(' ', order) + phrase + " ";
                
                for (int i = 0; i < padded.Length - order; i++)
                {
                    var prefix = padded.Substring(i, order);
                    var suffix = padded[i + order];
                    
                    if (!_markovChain.ContainsKey(prefix))
                        _markovChain[prefix] = new List<char>();
                    
                    _markovChain[prefix].Add(suffix);
                }
            }
        }

        /// <summary>
        /// Generates phrases using Markov chain
        /// </summary>
        public IEnumerable<string> GenerateMarkovPhrases(int count, int order = 2, int maxLength = 20)
        {
            var phrases = new List<string>();
            
            for (int i = 0; i < count * 2 && phrases.Count < count; i++)
            {
                var phrase = GenerateSingleMarkovPhrase(order, maxLength);
                if (!string.IsNullOrWhiteSpace(phrase) && phrase.Length >= 4)
                {
                    phrases.Add(phrase.Trim());
                }
            }

            return phrases.Distinct();
        }

        private string GenerateSingleMarkovPhrase(int order, int maxLength)
        {
            if (_markovChain.Count == 0)
                return null;

            var result = new StringBuilder(new string(' ', order));
            
            for (int i = 0; i < maxLength; i++)
            {
                var prefix = result.ToString(result.Length - order, order);
                
                if (_markovChain.TryGetValue(prefix, out var suffixes) && suffixes.Count > 0)
                {
                    var next = suffixes[_random.Next(suffixes.Count)];
                    if (next == ' ' && i > 3)
                        break;
                    result.Append(next);
                }
                else
                {
                    break;
                }
            }

            return result.ToString().Trim();
        }

        #endregion

        #region Model Persistence

        /// <summary>
        /// Saves the trained model to disk
        /// </summary>
        public async Task SaveModelAsync(string filePath)
        {
            var model = new MLModelState
            {
                WordFrequencies = _wordFrequencies,
                BigramFrequencies = _bigramFrequencies,
                TrigramFrequencies = _trigramFrequencies,
                KnownCompromised = _knownCompromisedPhrases.ToList(),
                MarkovChain = _markovChain.ToDictionary(
                    kvp => kvp.Key, 
                    kvp => new string(kvp.Value.ToArray())),
                PatternWeights = _patternModels.ToDictionary(m => m.Name, m => m.Weight),
                TrainedAt = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }

        /// <summary>
        /// Loads a trained model from disk
        /// </summary>
        public async Task LoadModelAsync(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            var json = await File.ReadAllTextAsync(filePath);
            var model = JsonSerializer.Deserialize<MLModelState>(json);

            if (model != null)
            {
                _wordFrequencies.Clear();
                foreach (var kvp in model.WordFrequencies)
                    _wordFrequencies[kvp.Key] = kvp.Value;

                _bigramFrequencies.Clear();
                foreach (var kvp in model.BigramFrequencies)
                    _bigramFrequencies[kvp.Key] = kvp.Value;

                _trigramFrequencies.Clear();
                foreach (var kvp in model.TrigramFrequencies)
                    _trigramFrequencies[kvp.Key] = kvp.Value;

                _knownCompromisedPhrases.Clear();
                _knownCompromisedPhrases.UnionWith(model.KnownCompromised);

                _markovChain.Clear();
                foreach (var kvp in model.MarkovChain)
                    _markovChain[kvp.Key] = kvp.Value.ToList();

                foreach (var pattern in _patternModels)
                {
                    if (model.PatternWeights.TryGetValue(pattern.Name, out var weight))
                        pattern.Weight = weight;
                }
            }
        }

        private class MLModelState
        {
            public Dictionary<string, double> WordFrequencies { get; set; }
            public Dictionary<string, double> BigramFrequencies { get; set; }
            public Dictionary<string, double> TrigramFrequencies { get; set; }
            public List<string> KnownCompromised { get; set; }
            public Dictionary<string, string> MarkovChain { get; set; }
            public Dictionary<string, double> PatternWeights { get; set; }
            public DateTime TrainedAt { get; set; }
        }

        #endregion

        #region Event Args

        public class PredictedPhrase
        {
            public string Phrase { get; set; }
            public double Confidence { get; set; }
            public string PatternType { get; set; }
        }

        public class PredictionEventArgs : EventArgs
        {
            public PredictedPhrase Prediction { get; set; }
        }

        public class TrainingProgressEventArgs : EventArgs
        {
            public int ProcessedItems { get; set; }
            public int TotalItems { get; set; }
            public string Message { get; set; }
        }

        #endregion
    }
}
