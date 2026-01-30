using DevExpress.XtraRichEdit.API.Native;
using DevTools.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DevTools.UI.Control
{
    [Browsable(true)]
    [ToolboxItem(true)]
    public partial class AIChat : UserControlBase
    {
        private string EYE_MODEL = "qwen2-vl";
        private string BRAIN_MODEL = "deepseek-r1-8b";//"qwen-coder-7b";
        private const string OLLAMA_URL = "http://localhost:11434/api/generate";
        private int _currentMaxToken = 8192;

        // GPU 모니터링용 타이머
        private Timer _gpuTimer;

        // 모델별 권장 컨텍스트 크기 매핑 (키워드 매칭)
        private readonly Dictionary<string, int> _modelTokenLimits = new Dictionary<string, int>
        {
            { "qwen2.5", 32768 },      // Qwen2.5는 긴 문맥 지원 (VRAM 충분 시)
            { "deepseek-r1", 8192 },   // DeepSeek R1 8B 표준
            { "llama3", 8192 },        // Llama3 표준
            { "mistral", 32768 },      // Mistral v0.3 등
            { "gemma", 8192 },         // Gemma 2
            { "phi", 4096 }            // MS Phi 시리즈 (구버전은 2048일 수 있음)
        };

        private static readonly HttpClient client = new HttpClient();
        // 대화 맥락(Context) 저장 리스트
        private List<object> _chatHistory = new List<object>();

        public AIChat()
        {
            InitializeComponent();
            client.Timeout = TimeSpan.FromMinutes(10);

            if (mproAiThink != null)
            {
                mproAiThink.Properties.Stopped = true;
                layAiThink.Visibility = DevExpress.XtraLayout.Utils.LayoutVisibility.Never;
            }

            LoadModelsToComboBox();

            // [추가] GPU 모니터링 타이머 설정 (2초마다 갱신)
            _gpuTimer = new Timer();
            _gpuTimer.Interval = 2000; 
            _gpuTimer.Tick += GpuTimer_Tick;
            _gpuTimer.Start();
        }

        private void GpuTimer_Tick(object sender, EventArgs e)
        {
            Task.Run(() =>
            {
                var info = GetNvidiaGpuInfo();

                this.Invoke(new Action(() =>
                {
                    // 데이터 수신 성공 시
                    if (info.MemoryTotal > 0)
                    {
                        // 1. 프로그레스바: GPU 코어 사용률 (0~100)
                        prgGpuUsage.EditValue = info.CoreLoad; 
                
                        // 2. 타이틀/텍스트: VRAM 정보 표시
                        // (사용하시는 컨트롤 속성에 맞게 주석 해제하여 사용하세요)
                        prgGpuUsage.Text = $"VRAM: {info.MemoryUsed} / {info.MemoryTotal} MB"; 
                        // groupControl1.Text = $"GPU Load: {info.CoreLoad}% | VRAM: {info.MemoryUsed}/{info.MemoryTotal}MB";
                    }
                    else
                    {
                        // [디버깅] 데이터를 못 가져오는 경우 텍스트로 표시
                        // prgGpuUsage.Text = "NVIDIA GPU 인식 불가";
                    }
                }));
            });
        }

        private GpuInfo GetNvidiaGpuInfo()
        {
            GpuInfo info = new GpuInfo();
            try
            {
                // nvidia-smi 기본 설치 경로 확인
                string nvidiaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        
                // 시스템 폴더(System32)에 없다면 Program Files 확인
                if (!File.Exists(nvidiaPath))
                {
                     nvidiaPath = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe";
                }

                // 그래도 없으면 단순히 명령어로 시도
                if (!File.Exists(nvidiaPath))
                {
                    nvidiaPath = "nvidia-smi";
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = nvidiaPath,
                    // 출력 형식: load, memory.used, memory.total
                    Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(psi))
                {
                    using (System.IO.StreamReader reader = process.StandardOutput)
                    {
                        string result = reader.ReadToEnd();
                        // 디버깅용: 출력값이 비어있는지 확인
                        // System.Diagnostics.Debug.WriteLine($"GPU Output: {result}");

                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            string[] parts = result.Split(',');
                            if (parts.Length >= 3)
                            {
                                int.TryParse(parts[0].Trim(), out info.CoreLoad);
                                int.TryParse(parts[1].Trim(), out info.MemoryUsed);
                                int.TryParse(parts[2].Trim(), out info.MemoryTotal);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 오류 발생 시 디버그 출력창에 원인 표시
                System.Diagnostics.Debug.WriteLine($"GPU Info Error: {ex.Message}");
            }
            return info;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter))
            {
                if (btnAnalyze.Enabled)
                {
                    BtnAnalyze_Click(this, EventArgs.Empty);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private async void LoadModelsToComboBox()
        {
            // 모델 리스트 가져오기
            var models = await GetOllamaModelsAsync();

            if (models.Count > 0)
            {
                cboModelList.Properties.Items.BeginUpdate();
                try
                {
                    cboModelList.Properties.Items.Clear();
                    foreach (string modelName in models)
                    {
                        cboModelList.Properties.Items.Add(new DevExpress.XtraEditors.Controls.ImageComboBoxItem(modelName, modelName, -1));
                    }
            
                    // 첫 번째 항목 선택
                    cboModelList.SelectedIndex = 0;
                }
                finally
                {
                    cboModelList.Properties.Items.EndUpdate();
                }
            }
            else
            {
                txtResult.Document.AppendText("[알림] 설치된 Ollama 모델을 찾을 수 없습니다.\r\n");
            }
        }

        /// <summary>
        /// Ollama에 설치된 모델 리스트를 가져옵니다.
        /// </summary>
        private async Task<List<string>> GetOllamaModelsAsync()
        {
            string url = "http://localhost:11434/api/tags";

            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var modelData = JsonSerializer.Deserialize<OllamaModelList>(responseBody, options);

                // 모델 이름만 추출하여 리스트로 반환
                return modelData?.Models.Select(m => m.Name).ToList() ?? new List<string>();
            }
            catch (Exception ex)
            {
                // 오류 시 빈 리스트 반환 혹은 로그 출력
                System.Diagnostics.Debug.WriteLine($"모델 조회 실패: {ex.Message}");
                return new List<string>();
            }
        }

        private async void BtnAnalyze_Click(object sender, EventArgs e)
        {
            try
            {
                this.Cursor = Cursors.WaitCursor;
                btnAnalyze.Enabled = false;

                if (mproAiThink != null)
                {
                    layAiThink.Visibility = DevExpress.XtraLayout.Utils.LayoutVisibility.Always;
                    mproAiThink.Properties.Stopped = false;
                }
                
                // 1. 입력 내용 추출
                var content = ExtractContentFromRichEdit();
                string userPrompt = content.Text;       
                string base64Image = content.ImageBase64; 
                string attContent = txtAttFile.Text;

                if (string.IsNullOrWhiteSpace(userPrompt) && string.IsNullOrEmpty(base64Image))
                {
                    MessageBox.Show("내용을 입력하거나 이미지를 붙여넣으세요.");
                    return;
                }

                // 2. 시스템 프롬프트 설정 (최초 1회)
                if (_chatHistory.Count == 0)
                {
                    string systemPrompt = chkUsePrompt.Checked ? txtSystemPrompt.Text : "";
                    if (string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        // DeepSeek 용
                        if (BRAIN_MODEL.ToLower().Contains("deepseek"))
                        {
                            systemPrompt = @"
You are an expert Senior Full Stack Engineer and Database Architect.
You possess deep knowledge of C#, Python, Vue.js, Oracle, and System Architecture.

[Instructions]
1.  **Analyze Intent**: First, determine if the user's input is a **Technical Question** (code, bug fix, architecture) or a **General Conversation** (greeting, self-intro, non-technical).
2.  **Reasoning Process**: Output your thinking process inside <think> tags in English.
3.  **Final Output**: After thinking, provide the final response strictly in **Professional Korean**.
    - **If Technical**: Explain the solution clearly and provide corrected or optimized code blocks.
    - **If General Conversation**: Answer naturally and politely. **DO NOT** provide code blocks unless explicitly requested.
    - Do NOT mix English in the final Korean explanation unless it is a technical term.

[Goal]
Your goal is to provide the most accurate technical solutions or be a helpful assistant depending on the user's need.
";
                        }
                        // Qwen 및 그 외
                        else
                        {
                            systemPrompt = @"
You are an expert Senior Full Stack Software Engineer and Database Architect.
Your expertise covers a wide range of technologies including C# (.NET), Python, Front-end (HTML, Vue.js), and Databases (Oracle, ANSI SQL).

Your responsibilities are:
1. Analyze the user's code or query regardless of the language.
2. Provide optimized, secure, and clean code solutions.
3. For Database queries, prioritize performance and explain execution logic.
4. If a bug is found, explain the root cause and fix it.
5. Think step-by-step and provide clear technical explanations.

Translate all your responses into professional Korean.";
                        }
                    }
                    _chatHistory.Add(new { role = "system", content = systemPrompt });

                    txtSystemPrompt.Text = systemPrompt;

                    // 시작 알림
                    PrintChatMessage("System", $"대화를 시작합니다. (Context Limit: {_currentMaxToken} Tokens)");
                }

                // 3. 첨부 파일 내용 추가
                if (!string.IsNullOrWhiteSpace(attContent))
                {
                    userPrompt += $"\r\n\r\n[Reference File Content]:\r\n{attContent}";
                }

                // 4. 이미지 처리
                if (!string.IsNullOrEmpty(base64Image))
                {
                    lblStatus.Text = $"[1단계] {EYE_MODEL} 이미지 분석 중...";
                    string ocrPrompt = "Please transcribe the text visible in this screenshot exactly.";
                    
                    // 이미지 분석 요청 (토큰 계산 불필요하므로 결과만 취함)
                    var ocrResult = await CallOllamaSingleAsync(EYE_MODEL, null, ocrPrompt, base64Image);
                    
                    userPrompt = $"[Image Text]:\r\n{ocrResult.ResponseText}\r\n\r\n[User Question]:\r\n{userPrompt}";
                    await UnloadModel(EYE_MODEL);
                }

                // 5. 사용자 질문 히스토리 추가
                _chatHistory.Add(new { role = "user", content = userPrompt });
                PrintChatMessage("User", string.IsNullOrEmpty(base64Image) ? userPrompt : "[이미지 포함 질문] " + userPrompt);

                // 6. 뇌(Brain) 모델 호출
                lblStatus.Text = $"[2단계] {BRAIN_MODEL} 답변 생성 중...";

                // API 호출 및 토큰 정보 수신
                var result = await CallOllamaHistoryAsync(BRAIN_MODEL, _chatHistory);
                
                if (result.IsSuccess)
                {
                    // AI 응답 히스토리 추가
                    _chatHistory.Add(new { role = "assistant", content = result.ResponseText });

                    // 답변 및 토큰 정보 출력
                    PrintChatMessage("AI", result.ResponseText);
                    PrintTokenUsage(result.PromptEvalCount, result.EvalCount);
                }
                else
                {
                    PrintChatMessage("Error", result.ResponseText);
                }

                await UnloadModel(BRAIN_MODEL);
                lblStatus.Text = "완료";
                txtQuest.Document.Text = ""; // 입력창 비우기
            }
            catch (Exception ex)
            {
                PrintChatMessage("Error", ex.Message);
                lblStatus.Text = "오류 발생";
            }
            finally
            {
                if (mproAiThink != null)
                {
                    mproAiThink.Properties.Stopped = true; // 애니메이션 멈춤
                    layAiThink.Visibility = DevExpress.XtraLayout.Utils.LayoutVisibility.Never;
                }

                btnAnalyze.Enabled = true;
                this.Cursor = Cursors.Default;
            }
        }

        // [신규] 토큰 사용량 출력 메서드
        private void PrintTokenUsage(int promptTokens, int responseTokens)
        {
            // Ollama는 전체 컨텍스트(이전 대화+현재 질문)를 prompt_eval_count로 반환함
            int totalUsed = promptTokens + responseTokens;
            int remaining = _currentMaxToken - totalUsed;
            double usagePercent = (double)totalUsed / _currentMaxToken * 100;

            string tokenInfo = $"[Token Usage] Used: {totalUsed} / {_currentMaxToken} ({usagePercent:F1}%) | Remaining: {remaining}";
            
            Document doc = txtResult.Document;
            doc.AppendText("\r\n");
            DocumentRange range = doc.AppendText(tokenInfo);
            
            CharacterProperties cp = doc.BeginUpdateCharacters(range);
            cp.FontSize = 8; // 작게 표시
            cp.ForeColor = remaining < 1000 ? Color.Red : Color.Gray; // 위험하면 빨간색
            doc.EndUpdateCharacters(cp);
            
            if (remaining < 1000)
            {
                doc.AppendText("\r\n[Warning] 컨텍스트 용량이 얼마 남지 않았습니다. 대화를 초기화하거나 요약해주세요.");
            }
            doc.AppendText("\r\n");
            txtResult.ScrollToCaret();
        }

        private void PrintChatMessage(string role, string message)
        {
            Document doc = txtResult.Document;
            doc.AppendText("\r\n──────────────────────────────────────────────────\r\n");

            DocumentRange range = doc.AppendText($"[{role}] ");
            CharacterProperties cp = doc.BeginUpdateCharacters(range);
            cp.Bold = true;
            if (role == "User") cp.ForeColor = Color.Blue;
            else if (role == "AI") cp.ForeColor = Color.Green;
            else if (role == "Error") cp.ForeColor = Color.Red;
            doc.EndUpdateCharacters(cp);

            doc.AppendText($"\r\n{message}\r\n");
            txtResult.Document.CaretPosition = txtResult.Document.CreatePosition(txtResult.Document.Range.End.ToInt());
            txtResult.ScrollToCaret();
        }

        // 결과 반환용 클래스
        private class OllamaResponse
        {
            public bool IsSuccess { get; set; }
            public string ResponseText { get; set; }
            public int PromptEvalCount { get; set; } // 입력+히스토리 토큰 수
            public int EvalCount { get; set; }       // 답변 토큰 수
        }

        private async Task<OllamaResponse> CallOllamaHistoryAsync(string modelName, List<object> historyMessages)
        {
            var requestData = new
            {
                model = modelName,
                messages = historyMessages,
                stream = false,
                options = new
                {
                    num_ctx = _currentMaxToken, // 8192
                    temperature = 0.6,
                    num_thread = 6
                }
            };

            return await SendOllamaRequest("http://localhost:11434/api/chat", requestData);
        }

        private async Task<OllamaResponse> CallOllamaSingleAsync(string modelName, string systemPrompt, string userPrompt, string base64Image = null)
        {
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new { role = "system", content = systemPrompt });
            
            var userMessage = new
            {
                role = "user",
                content = userPrompt,
                images = !string.IsNullOrEmpty(base64Image) ? new[] { base64Image } : null
            };
            messages.Add(userMessage);

            var requestData = new
            {
                model = modelName,
                messages = messages,
                stream = false,
                options = new { num_ctx = _currentMaxToken, temperature = 0.1 }
            };

            return await SendOllamaRequest("http://localhost:11434/api/chat", requestData);
        }

        private async Task<OllamaResponse> SendOllamaRequest(string url, object requestData)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            string jsonContent = JsonSerializer.Serialize(requestData, jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                HttpResponseMessage response = await client.PostAsync(url, content);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return new OllamaResponse { IsSuccess = false, ResponseText = $"[서버 오류 {response.StatusCode}] {responseBody}" };
                }

                using (JsonDocument doc = JsonDocument.Parse(responseBody))
                {
                    var root = doc.RootElement;
                    string text = "";
                    
                    if (root.TryGetProperty("message", out JsonElement msg) && msg.TryGetProperty("content", out JsonElement cnt))
                    {
                        text = cnt.GetString();
                    }
                    
                    // 토큰 정보 추출
                    int pEval = root.TryGetProperty("prompt_eval_count", out JsonElement pec) ? pec.GetInt32() : 0;
                    int eval = root.TryGetProperty("eval_count", out JsonElement ec) ? ec.GetInt32() : 0;

                    return new OllamaResponse { IsSuccess = true, ResponseText = text, PromptEvalCount = pEval, EvalCount = eval };
                }
            }
            catch (Exception ex)
            {
                return new OllamaResponse { IsSuccess = false, ResponseText = $"[예외 발생] {ex.Message}" };
            }
        }

        private (string Text, string ImageBase64) ExtractContentFromRichEdit()
        {
            string text = txtQuest.Document.Text.Trim();
            string imgBase64 = null;
            var images = txtQuest.Document.Images;

            if (images.Count > 0)
            {
                try
                {
                    DocumentImage docImage = images[0];
                    Image originalImage = docImage.Image.NativeImage;
                    if (originalImage != null)
                    {
                        using (Bitmap cleanBitmap = new Bitmap(originalImage.Width, originalImage.Height))
                        {
                            using (Graphics g = Graphics.FromImage(cleanBitmap))
                            {
                                g.Clear(Color.White);
                                g.DrawImage(originalImage, 0, 0, originalImage.Width, originalImage.Height);
                            }
                            using (MemoryStream ms = new MemoryStream())
                            {
                                cleanBitmap.Save(ms, ImageFormat.Jpeg);
                                imgBase64 = Convert.ToBase64String(ms.ToArray());
                            }
                        }
                    }
                }
                catch { }
            }
            return (text, imgBase64);
        }

        private async Task UnloadModel(string modelName)
        {
            try
            {
                var requestData = new { model = modelName, keep_alive = 0 };
                var content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json");
                await client.PostAsync(OLLAMA_URL, content);
            }
            catch { }
        }
        
        // 초기화 버튼 연결용 (Designer에서 버튼 클릭 이벤트에 연결하세요)
        private void btnResetChat_Click(object sender, EventArgs e)
        {
            _chatHistory.Clear();
            txtResult.Text = "";
            PrintChatMessage("System", "대화 내용이 초기화되었습니다.");
        }

        // 파일 처리 관련 기존 코드 유지 (btnOpenFile_Click, btnRemoveFile_Click, UpdateFileCont)
        private void btnOpenFile_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Multiselect = true;
                openFileDialog.Title = "파일을 선택하세요";
                openFileDialog.Filter = "모든 파일 (*.*)|*.*";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    foreach (string filePath in openFileDialog.FileNames)
                    {
                        if (!lstFiles.Items.Contains(filePath)) lstFiles.Items.Add(filePath);
                    }
                }
            }
            UpdateFileCont();
        }

        private void btnRemoveFile_Click(object sender, EventArgs e)
        {
            if (lstFiles.SelectedItems.Count > 0)
            {
                for (int i = lstFiles.SelectedIndices.Count - 1; i >= 0; i--)
                {
                    lstFiles.Items.RemoveAt(lstFiles.SelectedIndices[i]);
                }
                UpdateFileCont();
            }
        }

        void UpdateFileCont()
        {
            txtAttFile.BeginUpdate();
            txtAttFile.Text = "";
            try
            {
                foreach (string filePath in lstFiles.Items)
                {
                    if (File.Exists(filePath))
                    {
                        string fileContent = File.ReadAllText(filePath);
                        txtAttFile.Document.AppendText($"\n--- File: {Path.GetFileName(filePath)} ---\n");
                        txtAttFile.Document.AppendText(fileContent);
                        txtAttFile.Document.AppendText("\n\n");
                    }
                }
            }
            catch { }
            finally
            {
                txtAttFile.EndUpdate();
                txtAttFile.ScrollToCaret();
            }
        }

        private void cboModelList_EditValueChanged(object sender, EventArgs e)
        {
            if (cboModelList.EditValue == null) return;

            string selectedModel = BRAIN_MODEL = cboModelList.EditValue.ToString().ToLower();

            // 딕셔너리에서 키워드 검색 (기본값 8192)
            int newLimit = 8192;
            foreach (var kvp in _modelTokenLimits)
            {
                if (selectedModel.Contains(kvp.Key))
                {
                    newLimit = kvp.Value;
                    break;
                }
            }

            _currentMaxToken = newLimit;
        }
    }


    // GPU 정보 구조체
    public struct GpuInfo
    {
        public int CoreLoad;   // GPU 코어 사용률 (%)
        public int MemoryUsed; // 사용된 VRAM (MB)
        public int MemoryTotal;// 전체 VRAM (MB)
    }


    public class OllamaModelList
    {
        [JsonPropertyName("models")]
        public List<OllamaModelInfo> Models { get; set; }
    }

    public class OllamaModelInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    }
}
