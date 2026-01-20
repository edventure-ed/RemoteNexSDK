using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using System.Text;
using System.Globalization;

namespace RemoteNexLocalization
{
    public class RemoteNexTranslator : MonoBehaviour
    {
        public static RemoteNexTranslator Instance;

        [Header("Sunucu Ayarları")]
        public string backendUrl = "https://app-remotenex-server-eaeygufucgg5gkdg.westeurope-01.azurewebsites.net/api/translate";

        [Header("Oyunun Orijinal Dili")]
        [Tooltip("Oyunun geliştirildiği dili girin (örn: tr, en).")]
        public string sourceLanguageCode = "tr"; 

        [Header("Ayarlar")]
        public bool runInEditor = false;

        [Tooltip("SADECE EDİTÖRDE GEÇERLİDİR. Build alındığında burası yok sayılır ve cihaz dili alınır.")]
        public string overrideLanguage = ""; 

        public float fadeInDuration = 0.5f;

        [System.Serializable]
        public struct LanguageFont { public string languageCode; public TMP_FontAsset fontAsset; }
        public TMP_FontAsset defaultFont;
        public List<LanguageFont> specificFonts;

        [Header("Takip Edilecek Textler")]
        public List<TextMeshProUGUI> textsToTranslate;

        private Dictionary<int, string> lastKnownValues = new Dictionary<int, string>();
        private List<UnityWebRequest> activeRequests = new List<UnityWebRequest>();

        private string _currentDeviceLang;

        void Awake()
        {
            if (Instance == null) Instance = this;
            else { Destroy(gameObject); return; }
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            if (backendUrl.StartsWith("http://")) backendUrl = backendUrl.Replace("http://", "https://");

            _currentDeviceLang = GetDeviceLanguageCode();

            Debug.Log($"[RemoteNex] Başlatıldı. Kaynak: {sourceLanguageCode} -> Hedef (Cihaz): {_currentDeviceLang}");

            if (textsToTranslate == null || textsToTranslate.Count == 0) return;
            textsToTranslate.RemoveAll(item => item == null);

            foreach (var txt in textsToTranslate)
            {
                if (!lastKnownValues.ContainsKey(txt.GetInstanceID()))
                    lastKnownValues.Add(txt.GetInstanceID(), txt.text);
            }

            if (_currentDeviceLang == sourceLanguageCode)
            {
                Debug.Log("Cihaz dili orijinal dille aynı. Çeviri yapılmayacak.");
                foreach (var t in textsToTranslate) t.alpha = 1f;
                return;
            }

            foreach (var t in textsToTranslate) t.alpha = 0f;

            if (Application.isEditor && !runInEditor)
            {
                foreach (var t in textsToTranslate) t.alpha = 1f; 
                return;
            }

            ExecuteTranslation(textsToTranslate, true);
        }

        void Update()
        {
            if (_currentDeviceLang == sourceLanguageCode) return;
            if (Application.isEditor && !runInEditor) return;

            List<TextMeshProUGUI> changedTexts = new List<TextMeshProUGUI>();

            for (int i = 0; i < textsToTranslate.Count; i++)
            {
                var txtObj = textsToTranslate[i];
                if (txtObj == null) continue;

                int id = txtObj.GetInstanceID();
                string currentText = txtObj.text;

                if (!lastKnownValues.ContainsKey(id))
                {
                    lastKnownValues[id] = currentText;
                    continue;
                }

                if (lastKnownValues[id] != currentText)
                {
                    lastKnownValues[id] = currentText;

                    if (string.IsNullOrWhiteSpace(currentText))
                    {
                        txtObj.alpha = 1f;
                        continue;
                    }

                    txtObj.alpha = 0f;
                    changedTexts.Add(txtObj);
                }
            }

            if (changedTexts.Count > 0)
            {
                ExecuteTranslation(changedTexts, true);
            }
        }

        void ExecuteTranslation(List<TextMeshProUGUI> targets, bool useFadeEffect)
        {
            StartCoroutine(TranslateRoutine(targets, _currentDeviceLang, useFadeEffect));
        }

        IEnumerator TranslateRoutine(List<TextMeshProUGUI> targets, string targetLang, bool useFadeEffect)
        {
            List<string> rawTexts = new List<string>();
            List<TextMeshProUGUI> textTargets = new List<TextMeshProUGUI>();
            List<TextMeshProUGUI> allHiddenTargets = new List<TextMeshProUGUI>(targets);

            foreach (var item in targets)
            {
                if (item == null) continue;
                if (!double.TryParse(item.text, out _))
                {
                    rawTexts.Add(item.text);
                    textTargets.Add(item);
                }
            }

            if (rawTexts.Count == 0)
            {
                foreach (var t in allHiddenTargets) if (t) StartCoroutine(FadeTextIn(t));
                yield break;
            }

            TranslationRequest req = new TranslationRequest
            {
                SourceLang = sourceLanguageCode, 
                TargetLang = targetLang,         
                Texts = rawTexts
            };

            string jsonBody = JsonUtility.ToJson(req);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);

            int maxRetries = 3;
            int currentAttempt = 0;
            bool success = false;

            while (currentAttempt < maxRetries && !success)
            {
                currentAttempt++;
                UnityWebRequest request = new UnityWebRequest(backendUrl, "POST");
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("X-Game-Secret", "remotenex-super-secret-key-2026");

                activeRequests.Add(request);

                yield return request.SendWebRequest();

                if (activeRequests.Contains(request)) activeRequests.Remove(request);

                if (request.result == UnityWebRequest.Result.Success)
                {
                    success = true;
                    var data = JsonUtility.FromJson<TranslationResponse>(request.downloadHandler.text);

                    if (data != null && data.translations != null)
                    {
                        for (int i = 0; i < textTargets.Count; i++)
                        {
                            if (i < data.translations.Count && textTargets[i] != null)
                            {
                                string newText = data.translations[i];
                                var targetObj = textTargets[i];
                                targetObj.text = newText;
                                AssignFontForLanguage(targetObj, targetLang);
                                int id = targetObj.GetInstanceID();
                                if (lastKnownValues.ContainsKey(id)) lastKnownValues[id] = newText;
                            }
                        }
                    }
                }
                else
                {
                    if (currentAttempt < maxRetries) yield return new WaitForSeconds(1.0f);
                    else
                    {
                        foreach (var t in allHiddenTargets) if (t) t.alpha = 1f;
                    }
                }
                request.Dispose();
            }

            if (success)
            {
                foreach (var target in allHiddenTargets)
                {
                    if (target != null)
                    {
                        if (useFadeEffect) StartCoroutine(FadeTextIn(target));
                        else target.alpha = 1f;
                    }
                }
            }
        }

        private void AssignFontForLanguage(TextMeshProUGUI textObj, string langCode)
        {
            bool fontFound = false;
            foreach (var item in specificFonts)
            {
                if (item.languageCode == langCode && item.fontAsset != null)
                {
                    textObj.font = item.fontAsset;
                    fontFound = true;
                    break;
                }
            }
            if (!fontFound && defaultFont != null) textObj.font = defaultFont;
        }

        IEnumerator FadeTextIn(TextMeshProUGUI textObj)
        {
            float timer = 0f;
            while (timer < fadeInDuration)
            {
                if (textObj == null) yield break;
                timer += Time.deltaTime;
                textObj.alpha = Mathf.Lerp(0f, 1f, timer / fadeInDuration);
                yield return null;
            }
            if (textObj != null) textObj.alpha = 1f;
        }

        void OnDestroy() => CancelAllRequests();
        void OnApplicationQuit() => CancelAllRequests();
        private void CancelAllRequests() { foreach (var req in activeRequests) { if (req != null) { req.Abort(); req.Dispose(); } } activeRequests.Clear(); }

        private string GetDeviceLanguageCode()
        {
            if (Application.isEditor && !string.IsNullOrEmpty(overrideLanguage))
            {
                return overrideLanguage;
            }

            try { return CultureInfo.CurrentCulture.TwoLetterISOLanguageName; }
            catch { return "en"; }
        }
    }

    [System.Serializable]
    public class TranslationRequest
    {
        public string SourceLang;
        public string TargetLang;
        public List<string> Texts;
    }
    [System.Serializable]
    public class TranslationResponse
    {
        public List<string> translations;
    }
}