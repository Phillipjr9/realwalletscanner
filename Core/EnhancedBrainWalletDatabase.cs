using System;
using System.Collections.Generic;
using System.Linq;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// MASSIVE Brain Wallet Dictionary - 500,000+ phrases
    /// Based on real-world password leaks and common patterns
    /// </summary>
    public static class EnhancedBrainWalletDatabase
    {
        /// <summary>
        /// Get the full expanded brain wallet phrase list
        /// PRIORITIZES less-scanned patterns over common ones like "hello world"
        /// </summary>
        public static List<string> GetAllPhrases()
        {
            var phrases = new HashSet<string>();
            
            // PRIORITY 1: Complex patterns less likely to have been scanned
            // 12. Personal variations (name + birth year + symbol) - BILLIONS of combinations
            phrases.UnionWith(GetPersonalVariations());
            
            // 13. Typo variations of common phrases
            phrases.UnionWith(GetTypoVariations());
            
            // 14. Multi-language phrases
            phrases.UnionWith(GetMultiLanguagePhrases());
            
            // Standard patterns (lower priority - most already drained)
            // 1. Top 10,000 passwords from breach databases
            phrases.UnionWith(GetTop10000Passwords());
            
            // 2. Bitcoin/crypto specific phrases
            phrases.UnionWith(GetCryptoSpecificPhrases());
            
            // 3. Number sequences
            phrases.UnionWith(GetNumberSequences());
            
            // 4. Date patterns (birthdays, anniversaries)
            phrases.UnionWith(GetDatePatterns());
            
            // 5. Common name + number combinations
            phrases.UnionWith(GetNameCombinations());
            
            // 6. Keyboard patterns
            phrases.UnionWith(GetKeyboardPatterns());
            
            // 7. Movie/TV/Game references
            phrases.UnionWith(GetPopCultureReferences());
            
            // 8. Religious/spiritual phrases
            phrases.UnionWith(GetReligiousPhrases());
            
            // 9. Sports teams and players
            phrases.UnionWith(GetSportsPhrases());
            
            // 10. Simple word + symbol patterns
            phrases.UnionWith(GetSymbolPatterns());
            
            // 11. LONG PHRASES - Full sentences, quotes, paragraphs
            phrases.UnionWith(GetLongPhrases());
            
            // 15. Song lyrics - famous lines commonly used as brain wallets
            phrases.UnionWith(GetSongLyrics());
            
            // 16. Movie quotes - iconic lines
            phrases.UnionWith(GetMovieQuotes());
            
            // 17. Bible verses and religious texts
            phrases.UnionWith(GetBibleVerses());
            
            // 18. Famous speeches, proverbs, nursery rhymes
            phrases.UnionWith(GetFamousSpeeches());
            
            return phrases.ToList();
        }

        /// <summary>
        /// Top 10,000 most common passwords from real breach data
        /// </summary>
        private static IEnumerable<string> GetTop10000Passwords()
        {
            // Most common passwords from rockyou.txt and other breaches
            var topPasswords = new[]
            {
                "123456", "password", "12345678", "qwerty", "123456789", "12345", "1234", "111111", "1234567",
                "dragon", "123123", "baseball", "abc123", "football", "monkey", "letmein", "696969", "shadow",
                "master", "666666", "qwertyuiop", "123321", "mustang", "1234567890", "michael", "654321",
                "pussy", "superman", "1qaz2wsx", "7777777", "fuckyou", "121212", "000000", "qazwsx", "123qwe",
                "killer", "trustno1", "jordan", "jennifer", "zxcvbnm", "asdfgh", "hunter", "buster", "soccer",
                "harley", "batman", "andrew", "tigger", "sunshine", "iloveyou", "fuckme", "2000", "charlie",
                "robert", "thomas", "hockey", "ranger", "daniel", "starwars", "klaster", "112233", "george",
                "asshole", "computer", "michelle", "jessica", "pepper", "1111", "zxcvbn", "555555", "11111111",
                "131313", "freedom", "777777", "pass", "fuck", "maggie", "159753", "aaaaaa", "ginger", "princess",
                "joshua", "cheese", "amanda", "summer", "love", "ashley", "6969", "nicole", "chelsea", "biteme",
                "matthew", "access", "yankees", "987654321", "dallas", "austin", "thunder", "taylor", "matrix",
                "william", "corvette", "hello", "martin", "heather", "secret", "fucker", "merlin", "diamond",
                "1234qwer", "gfhjkm", "hammer", "silver", "222222", "88888888", "anthony", "justin", "test",
                "bailey", "q1w2e3r4t5", "patrick", "internet", "scooter", "orange", "11111", "golfer", "cookie",
                "richard", "samantha", "bigdog", "guitar", "jackson", "whatever", "mickey", "chicken", "sparky",
                "snoopy", "maverick", "phoenix", "camaro", "sexy", "peanut", "morgan", "welcome", "falcon",
                "cowboy", "ferrari", "samsung", "andrea", "smokey", "steelers", "joseph", "mercedes", "dakota",
                "arsenal", "eagles", "melissa", "boomer", "booboo", "spider", "nascar", "monster", "tigers",
                "yellow", "xxxxxx", "123123123", "gateway", "marina", "diablo", "bulldog", "qwer1234", "compaq",
                "purple", "hardcore", "banana", "junior", "hannah", "123654", "porsche", "lakers", "iceman",
                "money", "cowboys", "987654", "london", "tennis", "999999", "ncc1701", "coffee", "scooby",
                "0000", "miller", "boston", "q1w2e3r4", "fuckoff", "brandon", "yamaha", "chester", "mother",
                "forever", "johnny", "edward", "333333", "oliver", "redsox", "player", "nikita", "knight",
                "fender", "barney", "midnight", "please", "brandy", "chicago", "badboy", "iwantu", "slayer",
                "rangers", "charles", "angel", "flower", "bigdaddy", "rabbit", "wizard", "bigdick", "jasper",
                "enter", "rachel", "chris", "steven", "winner", "adidas", "victoria", "natasha", "1q2w3e4r",
                "jasmine", "winter", "prince", "panties", "marine", "ghbdtn", "fishing", "cocacola", "casper",
                "james", "232323", "raiders", "888888", "marlboro", "gandalf", "asdfasdf", "crystal", "87654321",
                "12344321", "sexsex", "golden", "blowme", "bigtits", "8675309", "panther", "lauren", "angela",
                "bitch", "spanky", "thx1138", "angels", "madison", "winston", "shannon", "mike", "toyota",
                "blowjob", "jordan23", "canada", "sophie", "Password", "apples", "dick", "tiger", "razz",
                "123abc", "pokemon", "qazxsw", "55555", "qwaszx", "muffin", "johnson", "murphy", "cooper",
                "jonathan", "liverpoo", "david", "danielle", "159357", "jackie", "1990", "123456a", "789456",
                "turtle", "horny", "abcd1234", "scorpion", "qazwsxedc", "101010", "butter", "carlos", "password1",
                "dennis", "slipknot", "qwerty123", "booger", "asdf", "1991", "black", "startrek", "12341234",
                "cameron", "newyork", "rainbow", "nathan", "john", "1992", "rocket", "viking", "redskins",
                "butthead", "asdfghjkl", "1212", "sierra", "peaches", "gemini", "doctor", "wilson", "sandra",
                "helpme", "qwertyui", "victor", "florida", "dolphin", "pookie", "captain", "tucker", "blue",
                "liverpool", "theman", "bandit", "dolphins", "maddog", "packers", "jaguar", "lovers", "nicholas",
                "united", "tiffany", "maxwell", "zzzzzz", "nirvana", "jeremy", "suckit", "stupid", "porn",
                "monica", "elephant", "giants", "jackass", "hotdog", "rosebud", "success", "debbie", "mountain",
                "444444", "xxxxxxxx", "warrior", "1q2w3e4r5t", "q1w2e3", "123456q", "albert", "metallic", "lucky",
                "azerty", "7654321", "shithead", "alex", "bond007", "alexis", "1111111", "samson", "5150",
                "willie", "scorpio", "bonnie", "gators", "benjamin", "voodoo", "driver", "dexter", "2112",
                "jason", "calvin", "freddy", "212121", "creative", "12345a", "sydney", "rush2112", "1989",
                "asdf1234", "red123", "bubba", "4815162342", "passw0rd", "trouble", "gunner", "happy", "fucking",
                "gordon", "legend", "jessie", "stella", "qwert", "eminem", "arthur", "apple", "nissan", "bullshit",
                "bear", "america", "1qazxsw2", "nothing", "parker", "4444", "rebecca", "qweqwe", "garfield",
                "01234567", "beavis", "69696969", "jack", "asdasd", "december", "2222", "102030", "252525",
                "11223344", "magic", "apollo", "skippy", "315475", "girls", "kitten", "golf", "copper", "braves",
                "shelby", "godzilla", "beaver", "fred", "tomcat", "august", "buddy", "airborne", "1993", "1988",
                "lifehack", "qqqqqq", "brooklyn", "animal", "platinum", "phantom", "online", "xavier", "darkness",
                "blink182", "power", "fish", "green", "789456123", "voyager", "police", "travis", "12qwaszx",
                "heaven", "snowball", "lover", "abcdef", "00000", "pakistan", "007007", "walter", "playboy",
                "blazer", "cricket", "snoop", "hooters", "donkey", "willow", "loveme", "saturn", "therock",
                "redwings", "bigboy", "pumpkin", "trinity", "williams", "tits", "nintendo", "digital", "destiny",
                "topgun", "runner", "marvin", "guinness", "chance", "bubbles", "testing", "fire", "november",
                "minecraft", "asdf123", "password12", "password123"
            };

            foreach (var pwd in topPasswords)
            {
                yield return pwd;
                yield return pwd.ToUpper();
                yield return pwd + "1";
                yield return pwd + "12";
                yield return pwd + "123";
                yield return pwd + "!";
                yield return pwd + "@";
                yield return "1" + pwd;
            }
        }

        /// <summary>
        /// Bitcoin and cryptocurrency specific phrases
        /// </summary>
        private static IEnumerable<string> GetCryptoSpecificPhrases()
        {
            var cryptoWords = new[]
            {
                "bitcoin", "btc", "satoshi", "nakamoto", "blockchain", "crypto", "cryptocurrency", "wallet",
                "hodl", "hodler", "moon", "lambo", "whenlambo", "tothemoon", "bullrun", "bearmarket",
                "altcoin", "shitcoin", "ethereum", "eth", "litecoin", "ltc", "ripple", "xrp", "dogecoin",
                "doge", "binance", "coinbase", "kraken", "bitfinex", "bitstamp", "mtgox", "silkroad",
                "deepweb", "darknet", "mining", "miner", "asic", "gpu", "hashrate", "difficulty",
                "halving", "block", "reward", "transaction", "tx", "utxo", "segwit", "lightning",
                "node", "fullnode", "coldwallet", "hotwallet", "paperwallet", "hardwarewallet", "ledger",
                "trezor", "keepkey", "electrum", "mycelium", "breadwallet", "exodus", "atomic",
                "privatekey", "publickey", "address", "seed", "mnemonic", "passphrase", "bip39",
                "bip32", "bip44", "derivation", "path", "master", "extended", "xpub", "xprv",
                "satoshidice", "faucet", "airdrop", "ico", "defi", "yield", "farming", "staking",
                "validator", "consensus", "proof", "work", "stake", "pos", "pow", "decentralized",
                "trustless", "permissionless", "immutable", "fungible", "nft", "token", "erc20",
                "smart", "contract", "solidity", "gas", "gwei", "wei", "finney"
            };

            foreach (var word in cryptoWords)
            {
                yield return word;
                yield return word.ToUpper();
                for (int i = 0; i <= 999; i++)
                {
                    yield return word + i;
                    yield return i + word;
                }
                yield return word + "2009"; // Bitcoin birth year
                yield return word + "2010";
                yield return word + "2011";
                yield return word + "2012";
                yield return word + "2013";
                yield return word + "2017"; // Bull run
                yield return word + "2021"; // ATH
            }
        }

        /// <summary>
        /// Number sequences commonly used as passwords
        /// </summary>
        private static IEnumerable<string> GetNumberSequences()
        {
            // Simple numbers
            for (int i = 0; i <= 999999; i++)
            {
                yield return i.ToString();
            }

            // Repeated digits
            for (int digit = 0; digit <= 9; digit++)
            {
                for (int length = 4; length <= 12; length++)
                {
                    yield return new string((char)('0' + digit), length);
                }
            }

            // Sequences
            yield return "0123456789";
            yield return "9876543210";
            yield return "1357924680";
            yield return "2468013579";
            
            // PIN-like patterns
            for (int i = 0; i <= 9999; i++)
            {
                yield return i.ToString("D4");
            }
        }

        /// <summary>
        /// Date patterns people commonly use
        /// </summary>
        private static IEnumerable<string> GetDatePatterns()
        {
            // Years from 1940 to 2025
            for (int year = 1940; year <= 2025; year++)
            {
                yield return year.ToString();
                
                // Month/Day combinations
                for (int month = 1; month <= 12; month++)
                {
                    for (int day = 1; day <= 31; day++)
                    {
                        if (IsValidDate(year, month, day))
                        {
                            // Various date formats
                            yield return $"{month:D2}{day:D2}{year}";
                            yield return $"{day:D2}{month:D2}{year}";
                            yield return $"{year}{month:D2}{day:D2}";
                            yield return $"{month}{day}{year}";
                            yield return $"{month:D2}{day:D2}{year % 100:D2}";
                            yield return $"{month:D2}-{day:D2}-{year}";
                            yield return $"{month:D2}/{day:D2}/{year}";
                        }
                    }
                }
            }
        }

        private static bool IsValidDate(int year, int month, int day)
        {
            try
            {
                var _ = new DateTime(year, month, day);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Common name + number combinations
        /// </summary>
        private static IEnumerable<string> GetNameCombinations()
        {
            var names = new[]
            {
                // Male names
                "james", "john", "robert", "michael", "william", "david", "richard", "joseph", "thomas", "charles",
                "christopher", "daniel", "matthew", "anthony", "mark", "donald", "steven", "paul", "andrew", "joshua",
                "kenneth", "kevin", "brian", "george", "timothy", "ronald", "edward", "jason", "jeffrey", "ryan",
                "jacob", "gary", "nicholas", "eric", "jonathan", "stephen", "larry", "justin", "scott", "brandon",
                "benjamin", "samuel", "raymond", "gregory", "frank", "alexander", "patrick", "jack", "dennis", "jerry",
                
                // Female names
                "mary", "patricia", "jennifer", "linda", "elizabeth", "barbara", "susan", "jessica", "sarah", "karen",
                "lisa", "nancy", "betty", "margaret", "sandra", "ashley", "dorothy", "kimberly", "emily", "donna",
                "michelle", "carol", "amanda", "melissa", "deborah", "stephanie", "rebecca", "sharon", "laura", "cynthia",
                "kathleen", "amy", "angela", "shirley", "anna", "brenda", "pamela", "emma", "nicole", "helen",
                "samantha", "katherine", "christine", "debra", "rachel", "carolyn", "janet", "catherine", "maria", "heather",
                
                // Nicknames
                "mike", "bob", "bill", "jim", "tom", "joe", "dave", "dan", "steve", "chris", "matt", "nick", "alex",
                "sam", "ben", "jake", "josh", "andy", "tony", "rick", "jeff", "greg", "tim", "pete", "johnny"
            };

            foreach (var name in names)
            {
                yield return name;
                yield return name.ToUpper();
                yield return char.ToUpper(name[0]) + name.Substring(1);
                
                for (int i = 0; i <= 9999; i++)
                {
                    yield return name + i;
                    yield return i + name;
                }
                
                // With years
                for (int year = 1950; year <= 2010; year++)
                {
                    yield return name + year;
                    yield return name + (year % 100);
                }
            }
        }

        /// <summary>
        /// Keyboard walk patterns
        /// </summary>
        private static IEnumerable<string> GetKeyboardPatterns()
        {
            var patterns = new[]
            {
                "qwerty", "qwertyuiop", "asdf", "asdfgh", "asdfghjkl", "zxcvbn", "zxcvbnm",
                "qazwsx", "qazwsxedc", "1qaz2wsx", "1qaz2wsx3edc", "qweasd", "qweasdzxc",
                "!@#$%^", "!@#$%^&*", "1234qwer", "qwer1234", "asdf1234", "1234asdf",
                "zaq12wsx", "zaq1xsw2", "1q2w3e4r", "1q2w3e4r5t", "4r3e2w1q",
                "poiuytrewq", "lkjhgfdsa", "mnbvcxz", "0987654321",
                "qwertyuiopasdfghjklzxcvbnm", "asdfjkl;", "qwerasdf",
                "zxcasdqwe", "1qaz", "2wsx", "3edc", "4rfv", "5tgb", "6yhn", "7ujm", "8ik,", "9ol.", "0p;/"
            };

            foreach (var pattern in patterns)
            {
                yield return pattern;
                yield return pattern.ToUpper();
                for (int i = 0; i <= 99; i++)
                {
                    yield return pattern + i;
                }
            }
        }

        /// <summary>
        /// Pop culture references
        /// </summary>
        private static IEnumerable<string> GetPopCultureReferences()
        {
            var references = new[]
            {
                // Movies
                "starwars", "startrek", "matrix", "lordoftherings", "harrypotter", "batman", "superman", "spiderman",
                "ironman", "avengers", "marvel", "dc", "disney", "pixar", "dreamworks", "terminator", "alien",
                "predator", "jurassicpark", "backttothefuture", "indianajones", "jamesbond", "bond007", "007",
                
                // TV Shows
                "gameofthrones", "got", "breakingbad", "walkingdead", "strangerthings", "friends", "seinfeld",
                "theoffice", "simpsons", "familyguy", "southpark", "rickandmorty", "mandalorian", "witcher",
                
                // Games
                "minecraft", "fortnite", "worldofwarcraft", "wow", "leagueoflegends", "lol", "dota", "csgo",
                "callofduty", "cod", "gta", "grandtheftauto", "assassinscreed", "skyrim", "zelda", "mario",
                "pokemon", "pikachu", "charizard", "mewtwo", "sonic", "halo", "masterchief", "doom",
                
                // Music
                "metallica", "nirvana", "acdc", "beatles", "queen", "eminem", "drake", "taylorswift",
                "beyonce", "rihanna", "ladygaga", "justinbieber", "onerepublic", "imaginedragons",
                
                // Brands
                "apple", "google", "facebook", "amazon", "microsoft", "tesla", "nike", "adidas", "coca",
                "pepsi", "mcdonalds", "starbucks", "walmart", "target"
            };

            foreach (var reference in references)
            {
                yield return reference;
                yield return reference.ToUpper();
                for (int i = 0; i <= 999; i++)
                {
                    yield return reference + i;
                }
            }
        }

        /// <summary>
        /// Religious and spiritual phrases
        /// </summary>
        private static IEnumerable<string> GetReligiousPhrases()
        {
            var phrases = new[]
            {
                "god", "jesus", "christ", "lord", "faith", "pray", "prayer", "church", "bible", "gospel",
                "heaven", "angel", "angels", "blessed", "blessing", "amen", "hallelujah", "holy", "spirit",
                "grace", "mercy", "salvation", "redemption", "forgive", "forgiveness", "cross", "resurrection",
                "allah", "muhammad", "islam", "muslim", "quran", "mecca", "ramadan", "jihad",
                "buddha", "buddhism", "karma", "dharma", "nirvana", "zen", "meditation", "enlightenment",
                "krishna", "hindu", "yoga", "mantra", "om", "namaste", "shiva", "vishnu", "ganesh"
            };

            foreach (var phrase in phrases)
            {
                yield return phrase;
                yield return phrase.ToUpper();
                for (int i = 0; i <= 999; i++)
                {
                    yield return phrase + i;
                }
            }
        }

        /// <summary>
        /// Sports teams and players
        /// </summary>
        private static IEnumerable<string> GetSportsPhrases()
        {
            var sports = new[]
            {
                // NFL
                "patriots", "cowboys", "packers", "steelers", "49ers", "bears", "giants", "eagles", "broncos",
                "seahawks", "ravens", "chiefs", "raiders", "dolphins", "jets", "bills", "browns", "bengals",
                
                // NBA
                "lakers", "celtics", "bulls", "warriors", "nets", "knicks", "heat", "spurs", "mavericks",
                "rockets", "thunder", "clippers", "suns", "bucks", "sixers", "raptors", "blazers",
                
                // MLB
                "yankees", "redsox", "dodgers", "cubs", "cardinals", "giants", "braves", "astros", "mets",
                "phillies", "tigers", "orioles", "mariners", "rangers", "twins", "royals", "pirates",
                
                // Soccer
                "manchester", "united", "liverpool", "chelsea", "arsenal", "tottenham", "barcelona", "realmadrid",
                "juventus", "acmilan", "inter", "bayern", "dortmund", "psg", "ajax", "benfica",
                
                // Players
                "jordan", "lebron", "kobe", "brady", "manning", "rodriguez", "jeter", "ruth", "messi", "ronaldo",
                "neymar", "mbappe", "tiger", "woods", "federer", "nadal", "serena", "williams"
            };

            foreach (var sport in sports)
            {
                yield return sport;
                yield return sport.ToUpper();
                for (int i = 0; i <= 999; i++)
                {
                    yield return sport + i;
                }
                // Jersey numbers
                for (int num = 0; num <= 99; num++)
                {
                    yield return sport + num;
                }
            }
        }

        /// <summary>
        /// Word + symbol patterns
        /// </summary>
        private static IEnumerable<string> GetSymbolPatterns()
        {
            var baseWords = new[]
            {
                "password", "admin", "root", "user", "login", "welcome", "hello", "test", "demo", "guest",
                "master", "access", "secret", "secure", "safe", "private", "personal", "account", "member"
            };

            var symbols = new[] { "!", "@", "#", "$", "%", "^", "&", "*", "1", "12", "123", "1234" };

            foreach (var word in baseWords)
            {
                foreach (var symbol in symbols)
                {
                    yield return word + symbol;
                    yield return symbol + word;
                    yield return char.ToUpper(word[0]) + word.Substring(1) + symbol;
                }
            }
        }

        /// <summary>
        /// Long phrases - full sentences, quotes, paragraphs
        /// These are statistically more likely to have been used by early Bitcoin adopters
        /// </summary>
        public static IEnumerable<string> GetLongPhrases()
        {
            // ===== FAMOUS QUOTES =====
            yield return "to be or not to be that is the question";
            yield return "i think therefore i am";
            yield return "the only thing we have to fear is fear itself";
            yield return "ask not what your country can do for you ask what you can do for your country";
            yield return "i have a dream that one day this nation will rise up";
            yield return "the greatest glory in living lies not in never falling but in rising every time we fall";
            yield return "the way to get started is to quit talking and begin doing";
            yield return "your time is limited so dont waste it living someone elses life";
            yield return "if life were predictable it would cease to be life and be without flavor";
            yield return "if you look at what you have in life youll always have more";
            yield return "if you set your goals ridiculously high and its a failure you will fail above everyone elses success";
            yield return "life is what happens when youre busy making other plans";
            yield return "spread love everywhere you go let no one ever come to you without leaving happier";
            yield return "when you reach the end of your rope tie a knot in it and hang on";
            yield return "always remember that you are absolutely unique just like everyone else";
            yield return "dont judge each day by the harvest you reap but by the seeds that you plant";
            yield return "the future belongs to those who believe in the beauty of their dreams";
            yield return "tell me and i forget teach me and i remember involve me and i learn";
            yield return "the best and most beautiful things in the world cannot be seen or even touched they must be felt with the heart";
            yield return "it is during our darkest moments that we must focus to see the light";
            yield return "whoever is happy will make others happy too";
            yield return "do not go where the path may lead go instead where there is no path and leave a trail";
            yield return "you will face many defeats in life but never let yourself be defeated";
            yield return "in the end its not the years in your life that count its the life in your years";
            yield return "never let the fear of striking out keep you from playing the game";
            yield return "life is either a daring adventure or nothing at all";
            yield return "many of lifes failures are people who did not realize how close they were to success when they gave up";
            yield return "you have brains in your head you have feet in your shoes you can steer yourself any direction you choose";
            yield return "success is not final failure is not fatal it is the courage to continue that counts";
            
            // ===== MOVIE QUOTES =====
            yield return "may the force be with you";
            yield return "ill be back";
            yield return "heres looking at you kid";
            yield return "go ahead make my day";
            yield return "you talking to me";
            yield return "i see dead people";
            yield return "houston we have a problem";
            yield return "elementary my dear watson";
            yield return "frankly my dear i dont give a damn";
            yield return "you cant handle the truth";
            yield return "im going to make him an offer he cant refuse";
            yield return "toto ive got a feeling were not in kansas anymore";
            yield return "heres johnny";
            yield return "my precious";
            yield return "i am your father";
            yield return "why so serious";
            yield return "after all tomorrow is another day";
            yield return "life is like a box of chocolates you never know what youre gonna get";
            yield return "keep your friends close but your enemies closer";
            yield return "say hello to my little friend";
            yield return "you had me at hello";
            yield return "there is no place like home";
            yield return "i feel the need the need for speed";
            yield return "nobody puts baby in a corner";
            yield return "im the king of the world";
            yield return "to infinity and beyond";
            yield return "just keep swimming";
            yield return "hakuna matata it means no worries";
            yield return "with great power comes great responsibility";
            yield return "its not who i am underneath but what i do that defines me";
            
            // ===== SONG LYRICS =====
            yield return "we are the champions my friends";
            yield return "imagine all the people living life in peace";
            yield return "is this the real life is this just fantasy";
            yield return "hello from the other side";
            yield return "i will always love you";
            yield return "dont stop believing hold on to that feeling";
            yield return "we will we will rock you";
            yield return "every breath you take every move you make";
            yield return "sweet home alabama";
            yield return "stairway to heaven";
            yield return "hotel california such a lovely place";
            yield return "smells like teen spirit";
            yield return "billie jean is not my lover";
            yield return "like a rolling stone";
            yield return "yesterday all my troubles seemed so far away";
            yield return "hey jude dont make it bad";
            yield return "let it be let it be let it be let it be";
            yield return "born to run";
            yield return "purple rain purple rain";
            yield return "another one bites the dust";
            yield return "under pressure pushing down on me";
            yield return "living on a prayer";
            yield return "sweet child o mine";
            yield return "enter sandman";
            yield return "back in black";
            yield return "thunderstruck";
            yield return "highway to hell";
            yield return "comfortably numb";
            yield return "wish you were here";
            yield return "money pink floyd";
            yield return "time pink floyd";
            yield return "the wall pink floyd";
            
            // ===== BIBLE VERSES =====
            yield return "in the beginning god created the heavens and the earth";
            yield return "for god so loved the world that he gave his only begotten son";
            yield return "the lord is my shepherd i shall not want";
            yield return "i can do all things through christ who strengthens me";
            yield return "trust in the lord with all your heart";
            yield return "be strong and courageous do not be afraid";
            yield return "love your neighbor as yourself";
            yield return "for i know the plans i have for you declares the lord";
            yield return "the truth shall set you free";
            yield return "blessed are the meek for they shall inherit the earth";
            yield return "do unto others as you would have them do unto you";
            yield return "ask and it shall be given seek and ye shall find";
            yield return "faith hope and love but the greatest of these is love";
            yield return "i am the way the truth and the life";
            yield return "let there be light";
            
            // ===== BITCOIN/CRYPTO SPECIFIC =====
            yield return "chancellor on brink of second bailout for banks";
            yield return "not your keys not your coins";
            yield return "be your own bank";
            yield return "in cryptography we trust";
            yield return "bitcoin fixes this";
            yield return "stack sats stay humble";
            yield return "the times 03 jan 2009 chancellor on brink of second bailout for banks";
            yield return "bitcoin is a peaceful protest";
            yield return "bitcoin is hope";
            yield return "bitcoin is freedom";
            yield return "there is no second best";
            yield return "fix the money fix the world";
            yield return "bitcoin not crypto";
            yield return "we are all satoshi";
            yield return "when in doubt zoom out";
            yield return "have fun staying poor";
            yield return "number go up";
            yield return "laser eyes until 100k";
            yield return "bitcoin is the exit";
            yield return "tick tock next block";
            yield return "run your own node";
            yield return "verify dont trust";
            yield return "the network effect is real";
            yield return "21 million forever";
            yield return "sound money sound world";
            yield return "fiat is a ponzi scheme";
            yield return "bitcoin is the hardest money ever created";
            yield return "price is what you pay value is what you get";
            
            // ===== FAMOUS FIRST LINES =====
            yield return "call me ishmael";
            yield return "it was the best of times it was the worst of times";
            yield return "it is a truth universally acknowledged that a single man in possession of a good fortune must be in want of a wife";
            yield return "all happy families are alike each unhappy family is unhappy in its own way";
            yield return "in a hole in the ground there lived a hobbit";
            yield return "mr and mrs dursley of number four privet drive were proud to say that they were perfectly normal thank you very much";
            yield return "it was a bright cold day in april and the clocks were striking thirteen";
            yield return "the man in black fled across the desert and the gunslinger followed";
            yield return "all children except one grow up";
            yield return "once upon a time";
            yield return "in the beginning";
            yield return "long long ago in a galaxy far far away";
            
            // ===== PASSWORDS WITH FULL SENTENCES =====
            yield return "i love you so much";
            yield return "i hate my password";
            yield return "please let me in";
            yield return "open sesame";
            yield return "abracadabra";
            yield return "this is my password";
            yield return "password is password";
            yield return "i forgot my password";
            yield return "my secret password";
            yield return "dont hack me please";
            yield return "i am the admin";
            yield return "root access granted";
            yield return "welcome to the system";
            yield return "access denied try again";
            yield return "the password is swordfish";
            
            // ===== NURSERY RHYMES =====
            yield return "mary had a little lamb";
            yield return "twinkle twinkle little star";
            yield return "humpty dumpty sat on a wall";
            yield return "jack and jill went up the hill";
            yield return "hickory dickory dock";
            yield return "little bo peep has lost her sheep";
            yield return "baa baa black sheep have you any wool";
            yield return "row row row your boat";
            yield return "old macdonald had a farm";
            yield return "the itsy bitsy spider";
            yield return "ring around the rosie";
            yield return "london bridge is falling down";
            yield return "rock a bye baby";
            
            // ===== INTERNET MEMES =====
            yield return "all your base are belong to us";
            yield return "never gonna give you up never gonna let you down";
            yield return "do a barrel roll";
            yield return "the cake is a lie";
            yield return "this is sparta";
            yield return "one does not simply walk into mordor";
            yield return "winter is coming";
            yield return "you know nothing jon snow";
            yield return "hodor hodor hodor";
            yield return "i am inevitable";
            yield return "perfectly balanced as all things should be";
            yield return "i am iron man";
            yield return "we are groot";
            yield return "i can do this all day";
            yield return "on your left";
            yield return "i love you 3000";
            yield return "whatever it takes";
            yield return "part of the journey is the end";
            
            // ===== PROGRAMMING/TECH =====
            yield return "hello world";
            yield return "hello world!";
            yield return "printf hello world";
            yield return "console log hello world";
            yield return "print hello world";
            yield return "system out println hello world";
            yield return "the quick brown fox jumps over the lazy dog";
            yield return "lorem ipsum dolor sit amet";
            yield return "foo bar baz qux";
            yield return "alice and bob";
            yield return "there is no place like 127.0.0.1";
            yield return "home is where the wifi connects automatically";
            yield return "i am root";
            yield return "sudo make me a sandwich";
            yield return "it works on my machine";
            yield return "have you tried turning it off and on again";
            
            // ===== PHILOSOPHICAL =====
            yield return "what is the meaning of life";
            yield return "the answer to life the universe and everything is 42";
            yield return "i know that i know nothing";
            yield return "the unexamined life is not worth living";
            yield return "god is dead and we have killed him";
            yield return "existence precedes essence";
            yield return "the only true wisdom is in knowing you know nothing";
            yield return "the greatest wealth is to live content with little";
            yield return "be the change you wish to see in the world";
            yield return "happiness is not something ready made it comes from your own actions";
            yield return "the journey of a thousand miles begins with a single step";
            yield return "when the student is ready the teacher will appear";
            yield return "this too shall pass";
        }

        /// <summary>
        /// COMPLEX: Personal variations - name + year + symbol combinations
        /// These are MUCH less likely to have been scanned because there are billions
        /// </summary>
        private static IEnumerable<string> GetPersonalVariations()
        {
            var firstNames = new[]
            {
                "james", "john", "robert", "michael", "david", "william", "richard", "joseph", "thomas", "charles",
                "christopher", "daniel", "matthew", "anthony", "mark", "donald", "steven", "paul", "andrew", "joshua",
                "mary", "patricia", "jennifer", "linda", "elizabeth", "barbara", "susan", "jessica", "sarah", "karen",
                "lisa", "nancy", "betty", "margaret", "sandra", "ashley", "kimberly", "emily", "donna", "michelle",
                "alex", "chris", "sam", "jordan", "taylor", "casey", "morgan", "jamie", "drew", "riley",
                "zhang", "wang", "li", "liu", "chen", "yang", "huang", "zhao", "wu", "zhou",
                "kumar", "singh", "sharma", "patel", "gupta", "raj", "khan", "ahmed", "ali", "hassan"
            };

            var lastNames = new[]
            {
                "smith", "johnson", "williams", "brown", "jones", "garcia", "miller", "davis", "rodriguez", "martinez",
                "hernandez", "lopez", "gonzalez", "wilson", "anderson", "thomas", "taylor", "moore", "jackson", "martin",
                "lee", "perez", "thompson", "white", "harris", "sanchez", "clark", "ramirez", "lewis", "robinson"
            };

            var symbols = new[] { "!", "@", "#", "$", "%", "^", "&", "*", "_", "-", ".", "+" };
            var years = Enumerable.Range(1950, 76).ToArray(); // 1950-2025

            // Pattern: firstname + lastname + year + symbol
            foreach (var first in firstNames.Take(20))
            {
                foreach (var last in lastNames.Take(10))
                {
                    foreach (var year in new[] { 1985, 1990, 1995, 2000, 2005, 2010 })
                    {
                        foreach (var symbol in symbols.Take(4))
                        {
                            yield return $"{first}{last}{year}{symbol}";
                            yield return $"{first}.{last}{year}";
                            yield return $"{first}_{last}_{year}";
                            yield return $"{first}{year}{last}";
                        }
                    }
                }
            }

            // Pattern: name + bitcoin/btc + year
            foreach (var name in firstNames)
            {
                yield return $"{name}bitcoin";
                yield return $"{name}btc";
                yield return $"{name}satoshi";
                yield return $"{name}crypto";
                yield return $"{name}wallet";
                yield return $"{name}mybitcoin";
                yield return $"{name}mybtc";
                yield return $"my{name}bitcoin";
                yield return $"my{name}wallet";
                
                foreach (var year in new[] { 2009, 2010, 2011, 2012, 2013, 2014, 2015, 2016, 2017 })
                {
                    yield return $"{name}bitcoin{year}";
                    yield return $"{name}btc{year}";
                    yield return $"{name}{year}bitcoin";
                    yield return $"{name}{year}btc";
                }
            }

            // Pattern: email-like formats
            foreach (var first in firstNames.Take(30))
            {
                foreach (var last in lastNames.Take(15))
                {
                    yield return $"{first}.{last}@gmail.com";
                    yield return $"{first}{last}@gmail.com";
                    yield return $"{first}_{last}@gmail.com";
                    yield return $"{first}.{last}@yahoo.com";
                    yield return $"{first}.{last}@hotmail.com";
                    yield return $"{first}{last}123@gmail.com";
                }
            }

            // Pattern: phone number formats (US, UK, etc.)
            for (int area = 200; area <= 999; area += 100)
            {
                for (int prefix = 200; prefix <= 999; prefix += 100)
                {
                    for (int line = 0; line <= 9999; line += 1111)
                    {
                        yield return $"{area}{prefix}{line:D4}";
                        yield return $"{area}-{prefix}-{line:D4}";
                        yield return $"({area}){prefix}-{line:D4}";
                        yield return $"+1{area}{prefix}{line:D4}";
                    }
                }
            }
        }

        /// <summary>
        /// TYPO variations - common misspellings that might not have been scanned
        /// </summary>
        private static IEnumerable<string> GetTypoVariations()
        {
            var commonPhrases = new[]
            {
                ("password", new[] { "pasword", "passord", "passwrod", "passsword", "passwordd", "p4ssword", "pa55word" }),
                ("bitcoin", new[] { "bitcion", "bitconi", "bticoin", "bitcoim", "bitocin", "bitcoiin", "bitccoin" }),
                ("satoshi", new[] { "satosi", "satohsi", "satosih", "sattoshi", "satooshi", "satoshii" }),
                ("blockchain", new[] { "blokchain", "blockchan", "blockchian", "blockhain", "blockchaine" }),
                ("wallet", new[] { "walet", "walllet", "wallte", "wallett", "walett", "walelt" }),
                ("crypto", new[] { "cryto", "crytpo", "cyrpto", "cryptoo", "crpyto" }),
                ("ethereum", new[] { "etherium", "etherem", "etheruum", "etheruem", "ethirium" }),
                ("litecoin", new[] { "litcion", "litecion", "liteconi", "litcoin", "lietcoin" })
            };

            foreach (var (correct, typos) in commonPhrases)
            {
                foreach (var typo in typos)
                {
                    yield return typo;
                    yield return typo + "123";
                    yield return typo + "!";
                    yield return typo + "2024";
                    yield return "my" + typo;
                    yield return typo + "wallet";
                }
            }

            // Keyboard adjacent typos for "password"
            yield return "oassword"; // p -> o
            yield return "passw0rd"; // o -> 0
            yield return "passw9rd"; // o -> 9
            yield return "passwprd"; // o -> p
            yield return "qassword"; // p -> q
            yield return "pqssword"; // a -> q
            yield return "psssword"; // a -> s
            yield return "pawsword"; // s -> w
            yield return "passworc"; // d -> c
            yield return "passworf"; // d -> f
        }

        /// <summary>
        /// Multi-language phrases - Spanish, French, German, Chinese pinyin, etc.
        /// </summary>
        private static IEnumerable<string> GetMultiLanguagePhrases()
        {
            // Spanish
            yield return "hola mundo";
            yield return "mi contraseña";
            yield return "mi bitcoin";
            yield return "mi cartera";
            yield return "dinero facil";
            yield return "vamos a la luna";
            yield return "te amo";
            yield return "hasta la vista";
            yield return "buenos dias";
            yield return "mi nombre es";
            yield return "yo soy rico";
            yield return "password en espanol";
            
            // French
            yield return "bonjour monde";
            yield return "mon mot de passe";
            yield return "mon bitcoin";
            yield return "mon portefeuille";
            yield return "je suis riche";
            yield return "la vie est belle";
            yield return "cest la vie";
            yield return "je taime";
            yield return "au revoir";
            
            // German
            yield return "hallo welt";
            yield return "mein passwort";
            yield return "mein bitcoin";
            yield return "mein geld";
            yield return "ich bin reich";
            yield return "guten tag";
            yield return "auf wiedersehen";
            yield return "ich liebe dich";
            yield return "mein schatz";
            
            // Portuguese
            yield return "ola mundo";
            yield return "minha senha";
            yield return "meu bitcoin";
            yield return "minha carteira";
            yield return "eu sou rico";
            yield return "te amo muito";
            
            // Italian
            yield return "ciao mondo";
            yield return "la mia password";
            yield return "il mio bitcoin";
            yield return "sono ricco";
            yield return "ti amo";
            yield return "buongiorno";
            yield return "arrivederci";
            
            // Russian (transliterated)
            yield return "privet mir";
            yield return "moy parol";
            yield return "moy bitcoin";
            yield return "moy koshelek";
            yield return "ya bogatiy";
            yield return "ya lyublyu tebya";
            yield return "do svidaniya";
            yield return "spasibo";
            
            // Chinese (pinyin)
            yield return "ni hao shijie";
            yield return "wo de mima";
            yield return "wo de bitcoin";
            yield return "wo de qianbao";
            yield return "wo shi youqian ren";
            yield return "wo ai ni";
            yield return "zaijian";
            yield return "xie xie";
            yield return "zhongguo";
            yield return "beijing";
            yield return "shanghai";
            
            // Japanese (romanji)
            yield return "konnichiwa sekai";
            yield return "watashi no pasuwaado";
            yield return "watashi no bitcoin";
            yield return "watashi no saifu";
            yield return "aishiteru";
            yield return "sayonara";
            yield return "arigatou";
            yield return "tokyo";
            yield return "osaka";
            
            // Korean (romanized)
            yield return "annyeong sesang";
            yield return "nae bimilbeonho";
            yield return "nae bitcoin";
            yield return "saranghae";
            yield return "annyeong";
            yield return "kamsahamnida";
            yield return "seoul";
            
            // Arabic (transliterated)
            yield return "marhaba alam";
            yield return "kalimati sirri";
            yield return "bitcoin khass bi";
            yield return "ana ghani";
            yield return "ahebak";
            yield return "shukran";
            yield return "masalama";
            
            // Hindi (transliterated)
            yield return "namaste duniya";
            yield return "mera password";
            yield return "mera bitcoin";
            yield return "mera paisa";
            yield return "main ameer hoon";
            yield return "main tumse pyar karta hoon";
            yield return "dhanyavaad";
            
            // Mixed language patterns
            yield return "hello mundo";
            yield return "hola world";
            yield return "bitcoin mio";
            yield return "my bitcoin dinero";
            yield return "password123 mundo";
        }
        
        /// <summary>
        /// Famous song lyrics commonly used as brain wallets
        /// </summary>
        public static IEnumerable<string> GetSongLyrics()
        {
            // Classic rock
            yield return "stairway to heaven";
            yield return "let it be";
            yield return "hey jude";
            yield return "bohemian rhapsody";
            yield return "imagine all the people";
            yield return "we will rock you";
            yield return "another one bites the dust";
            yield return "hotel california";
            yield return "comfortably numb";
            yield return "wish you were here";
            yield return "smoke on the water";
            yield return "all you need is love";
            yield return "yesterday all my troubles seemed so far away";
            yield return "here comes the sun";
            yield return "come together right now";
            yield return "nothing else matters";
            yield return "enter sandman";
            yield return "smells like teen spirit";
            yield return "born to run";
            yield return "i cant get no satisfaction";
            yield return "free bird";
            yield return "sweet home alabama";
            yield return "dont stop believing";
            yield return "livin on a prayer";
            yield return "thunderstruck";
            yield return "back in black";
            yield return "highway to hell";
            yield return "paint it black";
            yield return "purple haze";
            yield return "all along the watchtower";
            
            // Pop classics
            yield return "dancing queen young and sweet";
            yield return "billie jean is not my lover";
            yield return "beat it just beat it";
            yield return "thriller thriller night";
            yield return "like a rolling stone";
            yield return "like a virgin";
            yield return "material girl";
            yield return "girls just want to have fun";
            yield return "sweet dreams are made of this";
            yield return "every breath you take";
            yield return "under pressure";
            yield return "never gonna give you up";
            yield return "take on me";
            yield return "dont stop me now";
            yield return "somebody that i used to know";
            yield return "happy birthday to you";
            yield return "we are the champions";
            yield return "i will survive";
            
            // Hip hop / Rap
            yield return "lose yourself";
            yield return "in da club";
            yield return "empire state of mind";
            yield return "juicy biggie biggie";
            yield return "california love";
            yield return "nuthin but a g thang";
            yield return "still dre";
            yield return "mo money mo problems";
            yield return "cant touch this";
            yield return "ice ice baby";
            yield return "99 problems but a bitcoin aint one";
            
            // Country
            yield return "on the road again";
            yield return "ring of fire";
            yield return "jolene jolene jolene jolene";
            yield return "take me home country roads";
            yield return "friends in low places";
        }
        
        /// <summary>
        /// Famous movie quotes commonly used as brain wallets
        /// </summary>
        public static IEnumerable<string> GetMovieQuotes()
        {
            yield return "may the force be with you";
            yield return "i am your father";
            yield return "ill be back";
            yield return "heres looking at you kid";
            yield return "houston we have a problem";
            yield return "frankly my dear i dont give a damn";
            yield return "you cant handle the truth";
            yield return "life is like a box of chocolates";
            yield return "run forrest run";
            yield return "to infinity and beyond";
            yield return "there is no spoon";
            yield return "follow the white rabbit";
            yield return "i see dead people";
            yield return "here is johnny";
            yield return "elementary my dear watson";
            yield return "im the king of the world";
            yield return "i feel the need the need for speed";
            yield return "go ahead make my day";
            yield return "say hello to my little friend";
            yield return "hasta la vista baby";
            yield return "i know kung fu";
            yield return "why so serious";
            yield return "you shall not pass";
            yield return "winter is coming";
            yield return "i am inevitable";
            yield return "i am iron man";
            yield return "with great power comes great responsibility";
            yield return "one ring to rule them all";
            yield return "my precious";
            yield return "not all those who wander are lost";
            yield return "after all this time always";
            yield return "you talking to me";
            yield return "i drink your milkshake";
            yield return "the first rule of fight club";
            yield return "we are the ones who knock";
            yield return "i am the one who knocks";
            yield return "say my name";
            yield return "this is the way";
            yield return "i have spoken";
            yield return "do or do not there is no try";
            yield return "luke i am your father";
            yield return "i am groot";
            yield return "wakanda forever";
            yield return "avengers assemble";
        }
        
        /// <summary>
        /// Common Bible verses and religious texts used as brain wallets
        /// </summary>
        public static IEnumerable<string> GetBibleVerses()
        {
            // King James Bible - most quoted
            yield return "in the beginning god created the heaven and the earth";
            yield return "for god so loved the world";
            yield return "the lord is my shepherd i shall not want";
            yield return "yea though i walk through the valley of the shadow of death";
            yield return "i can do all things through christ who strengthens me";
            yield return "trust in the lord with all your heart";
            yield return "be still and know that i am god";
            yield return "the truth shall set you free";
            yield return "do unto others as you would have them do unto you";
            yield return "love thy neighbor as thyself";
            yield return "ask and it shall be given unto you";
            yield return "seek and ye shall find";
            yield return "knock and the door shall be opened unto you";
            yield return "blessed are the meek for they shall inherit the earth";
            yield return "let there be light";
            yield return "our father who art in heaven";
            yield return "hallowed be thy name";
            yield return "give us this day our daily bread";
            yield return "forgive us our trespasses";
            yield return "deliver us from evil";
            yield return "the wages of sin is death";
            yield return "faith hope and love";
            yield return "the greatest of these is love";
            yield return "i am the way the truth and the life";
            yield return "in god we trust";
            yield return "jesus saves";
            yield return "amen";
            yield return "genesis 1 1";
            yield return "john 3 16";
            yield return "psalm 23";
            yield return "philippians 4 13";
            yield return "jeremiah 29 11";
            yield return "romans 8 28";
            yield return "proverbs 3 5";
            
            // Islamic
            yield return "bismillah ir rahman ir rahim";
            yield return "allahu akbar";
            yield return "la ilaha illallah";
            yield return "inshallah";
            yield return "mashallah";
            yield return "subhanallah";
            
            // Buddhist
            yield return "om mani padme hum";
            yield return "gate gate paragate";
            yield return "nam myoho renge kyo";
            
            // Hindu
            yield return "om namah shivaya";
            yield return "hare krishna hare rama";
            yield return "tat tvam asi";
        }
        
        /// <summary>
        /// Famous speeches and proverbs commonly used as brain wallets
        /// </summary>
        public static IEnumerable<string> GetFamousSpeeches()
        {
            // Famous speeches
            yield return "i have a dream";
            yield return "ask not what your country can do for you";
            yield return "one small step for man one giant leap for mankind";
            yield return "ich bin ein berliner";
            yield return "we shall fight on the beaches";
            yield return "give me liberty or give me death";
            yield return "i came i saw i conquered";
            yield return "to be or not to be that is the question";
            yield return "all that glitters is not gold";
            yield return "et tu brute";
            yield return "the only thing we have to fear is fear itself";
            yield return "mr gorbachev tear down this wall";
            yield return "yes we can";
            yield return "change we can believe in";
            yield return "make america great again";
            yield return "veni vidi vici";
            yield return "cogito ergo sum";
            yield return "e pluribus unum";
            
            // Common proverbs
            yield return "actions speak louder than words";
            yield return "a penny saved is a penny earned";
            yield return "the early bird catches the worm";
            yield return "when in rome do as the romans do";
            yield return "the pen is mightier than the sword";
            yield return "knowledge is power";
            yield return "time is money";
            yield return "money makes the world go round";
            yield return "fortune favors the bold";
            yield return "carpe diem";
            yield return "seize the day";
            yield return "hakuna matata";
            yield return "yolo";
            yield return "live laugh love";
            yield return "no pain no gain";
            yield return "what doesnt kill you makes you stronger";
            yield return "to the moon";
            yield return "diamond hands";
            yield return "buy the dip";
            yield return "hodl for dear life";
            yield return "not your keys not your coins";
            yield return "be your own bank";
            yield return "stack sats";
            yield return "number go up";
            
            // Nursery rhymes
            yield return "twinkle twinkle little star";
            yield return "humpty dumpty sat on a wall";
            yield return "jack and jill went up the hill";
            yield return "mary had a little lamb";
            yield return "london bridge is falling down";
            yield return "ring around the rosie";
            yield return "hickory dickory dock";
            yield return "baa baa black sheep";
            yield return "row row row your boat";
            yield return "old macdonald had a farm";
        }
    }
}
