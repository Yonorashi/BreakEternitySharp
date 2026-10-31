#if UNITY_5_3_OR_NEWER
using System;
using UnityEngine;

namespace BreakEternity.Examples
{
    /// <summary>
    /// Optional example (BigDouble does not need it): a resource, generators with a rising price, "Buy 1" and "Buy max",
    /// display, saving and loading with the migration of old double saves. Add it as a component to an empty GameObject
    /// and press Play.
    /// </summary>
    public class BigDoubleExample : MonoBehaviour
    {
        // Start values in the Inspector: entered as text, e.g. "10", "1e1000" or "ee20".
        [SerializeField] private BigDouble startJoule = 10;
        [SerializeField] private BigDouble priceStart = 10;
        [SerializeField] private double priceRatio = 1.15;
        [SerializeField] private BigDouble productionPerGenerator = 1;

        private BigDouble joule;
        private BigDouble generators; // a BigDouble count can grow beyond long
        private BigDouble multiplier = 1;

        private const string SaveKey = "BigDoubleExampleSave";

        private void Start()
        {
            if (!Load())
            {
                // New game: reset all fields, not just some of them.
                joule = startJoule;
                generators = 0;
                multiplier = 1;
            }
        }

        private void Update()
        {
            // Works like double; Time.deltaTime (float) converts automatically.
            joule += generators * productionPerGenerator * multiplier * Time.deltaTime;
        }

        // Price of the next generator. BigDouble.Pow, not Math.Pow: Math.Pow would turn large prices into Infinity
        // before BigDouble gets them.
        public BigDouble NextPrice => priceStart * BigDouble.Pow(priceRatio, generators);

        public void BuyOne()
        {
            BigDouble price = NextPrice;
            if (joule >= price)
            {
                joule -= price;
                generators++;
            }
        }

        public void BuyMax()
        {
            // The largest count whose total price fits the balance, and exactly that total price.
            BigDouble count = BigDouble.AffordGeometricSeries(joule, priceStart, priceRatio, generators);
            if (count > 0)
            {
                joule -= BigDouble.SumGeometricSeries(count, priceStart, priceRatio, generators);
                generators += count;
            }
        }

        private void OnGUI()
        {
            // A simple display with standard formats; game notation (K, M, aa ...) belongs in your own number formatter.
            GUILayout.Label("Joule: " + joule.ToString("G4"));
            GUILayout.Label("Generators: " + generators.ToString("N0") + ", next price: " + NextPrice.ToString("G4"));

            // Unity APIs expect float. InverseLerp always returns the progress to the next purchase between 0 and 1, where
            // the conversion to float is safe. Use TryToFloat for arbitrary values.
            float progress = (float)BigDouble.InverseLerp(0, NextPrice, joule);
            GUILayout.HorizontalSlider(progress, 0f, 1f);
            if (generators.TryToFloat(out float generatorsAsFloat))
            {
                GUILayout.Label("As float: " + generatorsAsFloat);
            }

            if (GUILayout.Button("Buy 1"))
            {
                BuyOne();
            }
            if (GUILayout.Button("Buy max"))
            {
                BuyMax();
            }
            if (GUILayout.Button("Save"))
            {
                Save();
            }
        }

        public void Save()
        {
            var data = new ExampleSaveData
            {
                version = ExampleSaveData.CurrentVersion,
                joule = joule,
                generators = generators,
                multiplier = multiplier,
            };
            PlayerPrefs.SetString(SaveKey, JsonUtility.ToJson(data));
            PlayerPrefs.Save();
        }

        public bool Load()
        {
            if (!PlayerPrefs.HasKey(SaveKey))
            {
                return false;
            }
            // Read and check everything into local variables first, then apply it together: a damaged value must not
            // leave the running state half overwritten.
            ExampleSaveData data;
            try
            {
                data = ExampleSaveData.FromJson(PlayerPrefs.GetString(SaveKey));
            }
            catch (ArgumentException)
            {
                data = null; // not valid JSON
            }
            if (data == null
                || !data.joule.TryGetValue(out BigDouble loadedJoule)
                || !data.generators.TryGetValue(out BigDouble loadedGenerators)
                || !data.multiplier.TryGetValue(out BigDouble loadedMultiplier))
            {
                Debug.LogWarning("Save is damaged, starting a new game.");
                return false;
            }
            joule = loadedJoule;
            generators = loadedGenerators;
            multiplier = loadedMultiplier;
            return true;
        }
    }

    /// <summary>
    /// Save from version 2 on: all large numbers as SerializableBigDouble (lossless text, e.g.
    /// {"value":"(e^1)1000000000"}). Older saves with double fields are converted when loading.
    /// </summary>
    [Serializable]
    public class ExampleSaveData
    {
        public const int CurrentVersion = 2;

        // Deliberately without a start value: old saves do not have this field and are recognized as version 0/1.
        public int version;

        public SerializableBigDouble joule;
        public SerializableBigDouble generators;

        // A field added later, with the neutral value 1 (missing from older saves, see FromJson).
        public SerializableBigDouble multiplier = 1;

        /// <summary>Reads a save of any version; null for empty text (invalid JSON throws ArgumentException).</summary>
        public static ExampleSaveData FromJson(string json)
        {
            var header = JsonUtility.FromJson<VersionOnly>(json);
            if (header == null)
            {
                return null;
            }
            ExampleSaveData data;
            if (header.version >= 2)
            {
                data = JsonUtility.FromJson<ExampleSaveData>(json);
            }
            else
            {
                // Version 1 had the same field names, but as double: read with the old class and convert.
                var old = JsonUtility.FromJson<ExampleSaveDataV1>(json);
                data = new ExampleSaveData { joule = old.joule, generators = old.generators };
            }
            // A missing field gives empty text (= 0). For multipliers, set the neutral value 1 explicitly.
            if (string.IsNullOrEmpty(data.multiplier.SerializedValue))
            {
                data.multiplier = 1;
            }
            data.version = CurrentVersion;
            return data;
        }

        /// <summary>Reads only the version number of a save.</summary>
        [Serializable]
        public class VersionOnly
        {
            public int version;
        }
    }

    /// <summary>Old save (version 1): values as double. Only read, never written.</summary>
    [Serializable]
    public class ExampleSaveDataV1
    {
        public int version;
        public double joule;
        public double generators;
    }
}
#endif
