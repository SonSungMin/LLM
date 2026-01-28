using DevExpress.XtraRichEdit.API.Native;
using DevTools.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
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
        private const string EYE_MODEL = "qwen2-vl";
        private const string BRAIN_MODEL = "deepseek-v2";//"qwen-coder-7b";
        private const string OLLAMA_URL = "http://localhost:11434/api/generate";

        private static readonly HttpClient client = new HttpClient();

        public AIChat()
        {
            InitializeComponent();
            client.Timeout = TimeSpan.FromMinutes(10); // 타임아웃 10분으로 넉넉하게
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // [Ctrl] + [Enter] 키가 눌렸는지 확인
            if (keyData == (Keys.Control | Keys.Enter))
            {
                // 버튼이 사용 가능한 상태(분석 중이 아님)일 때만 실행
                if (btnAnalyze.Enabled)
                {
                    // 버튼 클릭 이벤트 강제 호출
                    BtnAnalyze_Click(this, EventArgs.Empty);
                    return true; // 키 입력을 여기서 처리했음을 시스템에 알림 (딩~ 소리 방지)
                }
            }

            // 그 외의 키는 원래대로 처리
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private async void BtnAnalyze_Click(object sender, EventArgs e)
        {
            try
            {
                this.Cursor = Cursors.WaitCursor;
                btnAnalyze.Enabled = false;
                
                // 1. RichEditControl에서 텍스트와 이미지를 분리 추출
                var content = ExtractContentFromRichEdit();

                string userPrompt = content.Text;       // 사용자가 적은 글
                string base64Image = content.ImageBase64; // 사용자가 붙여넣은 이미지 (없으면 null)
                string attContent = txtAttFile.Text;

                txtResult.Text = "";
                string systemPrompt = chkUsePrompt.Checked ? txtSystemPrompt.Text : "";

                // -------------------------------------------------------
                // 시나리오 A: 이미지가 있는 경우 (이미지 분석 -> 뇌 전달)
                // -------------------------------------------------------
                if (!string.IsNullOrEmpty(base64Image))
                {
                    lblStatus.Text = $"[1단계] {EYE_MODEL} 이미지 텍스트 추출 중...";
                    txtResult.Document.AppendText($"=== [Type: 이미지 + 텍스트] 분석 시작 ===\r\n");

                    // 1-1. 눈(Eye)에게 OCR 요청
                    string ocrPrompt = @"
You are a helpful assistant for data entry and documentation.
Please transcribe the text visible in this software interface screenshot.
This is for documenting the user interface layout.
Output the text content exactly as it appears, maintaining the structure.
";

                    string extractedTextFromImage = await CallOllamaAsync(EYE_MODEL, ocrPrompt, userPrompt, base64Image);

                    txtResult.Document.AppendText($"[추출된 이미지 내용]\r\n{extractedTextFromImage}\r\n\r\n");

                    // 메모리 해제
                    await UnloadModel(EYE_MODEL);

                    // 1-2. 뇌(Brain)에게 통합 분석 요청
                    lblStatus.Text = $"[2단계] {BRAIN_MODEL} 최종 분석 중...";

                    userPrompt = !string.IsNullOrEmpty(extractedTextFromImage) ? $"{extractedTextFromImage}\r\n{userPrompt}" : "";

                    string finalResult = await CallOllamaAsync(BRAIN_MODEL, systemPrompt, userPrompt, null);
                    txtResult.Document.AppendText($"=== [최종 결과] ===\r\n{finalResult}");
                }
                // -------------------------------------------------------
                // 시나리오 B: 이미지가 없는 경우 (텍스트만 뇌로 전달)
                // -------------------------------------------------------
                else
                {
                    if (string.IsNullOrWhiteSpace(userPrompt))
                    {
                        MessageBox.Show("내용을 입력하거나 이미지를 붙여넣으세요.");
                        return;
                    }

                    lblStatus.Text = $"[단일 단계] {BRAIN_MODEL} 텍스트 분석 중...";
                    txtResult.Document.AppendText($"=== [Type: 텍스트 전용] 분석 시작 ===\r\n");
                    
                    txtResult.Document.AppendText($"=== 최종 프롬프트 ===\r\n");
                    txtResult.Document.AppendText($"[ 시스템 프롬프트 ]\r\n");
                    txtResult.Document.AppendText(systemPrompt + "\r\n\r\n");
                    txtResult.Document.AppendText($"[ 사용자 프롬프트 ]\r\n");
                    txtResult.Document.AppendText(userPrompt + "\r\n\r\n");
                    txtResult.Document.AppendText($"**********************************************************\r\n");

                    string finalResult = await CallOllamaAsync(BRAIN_MODEL, systemPrompt, userPrompt, null);
                    txtResult.Document.AppendText(finalResult);

                    txtResult.Document.CaretPosition = txtResult.Document.CreatePosition(txtResult.Document.Range.End.ToInt());
                }

                // 끝난 후 Brain 모델 메모리 정리
                await UnloadModel(BRAIN_MODEL);
                lblStatus.Text = "완료";
            }
            catch (Exception ex)
            {
                txtResult.Document.AppendText($"\r\n[Error] {ex.Message}");
                lblStatus.Text = "오류 발생";
            }
            finally
            {
                btnAnalyze.Enabled = true;
                this.Cursor = Cursors.Default;
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

                    // [오류 수정] 여기에 'using'을 절대 쓰면 안 됩니다!
                    // 화면에 살아있는 이미지를 참조만 해야 합니다.
                    Image originalImage = docImage.Image.NativeImage;

                    if (originalImage != null)
                    {
                        // 원본은 건드리지 말고, '새 하얀 도화지(Bitmap)'에 복사해서 처리합니다.
                        // 이렇게 하면 원본 이미지가 보호되어 클릭해도 오류가 안 납니다.
                        using (Bitmap cleanBitmap = new Bitmap(originalImage.Width, originalImage.Height))
                        {
                            using (Graphics g = Graphics.FromImage(cleanBitmap))
                            {
                                // 흰 배경 깔기 (투명도 문제 해결)
                                g.Clear(Color.White);
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                                // 원본 그림을 복사본에 그리기
                                g.DrawImage(originalImage, 0, 0, originalImage.Width, originalImage.Height);
                            }

                            // 복사본을 JPEG로 변환
                            using (MemoryStream ms = new MemoryStream())
                            {
                                cleanBitmap.Save(ms, ImageFormat.Jpeg);
                                imgBase64 = Convert.ToBase64String(ms.ToArray());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"이미지 처리 오류: {ex.Message}");
                }
            }

            return (text, imgBase64);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="modelName">모델명</param>
        /// <param name="systemPrompt">시스템 프롬프트</param>
        /// <param name="userPrompt">사용자 프롬프트</param>
        /// <param name="base64Image">이미지</param>
        /// <returns></returns>
        private async Task<string> CallOllamaAsync(string modelName, string systemPrompt, string userPrompt, string base64Image = null)
        {
            string chatUrl = "http://localhost:11434/api/chat";

            // 1. 메시지 리스트 동적 구성
            var messages = new List<object>();

            // 시스템 프롬프트가 있다면 맨 앞에 추가
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new { role = "system", content = systemPrompt });
            }

            // 사용자 메시지 구성 (이미지 포함 여부 체크)
            var userMessage = new
            {
                role = "user",
                content = userPrompt,
                images = !string.IsNullOrEmpty(base64Image) ? new[] { base64Image } : null
            };
            messages.Add(userMessage);

            // 2. 요청 데이터 구성
            var requestData = new
            {
                model = modelName,
                messages = messages,
                stream = false,
                options = new
                {
                    num_ctx = 8192,    // 코딩 작업은 컨텍스트가 길어야 하므로 8192 권장
                    temperature = 0.1, // 코딩 정확도를 위해 낮춤
                    top_p = 0.9,
                    repeat_penalty = 1.1,
                    num_thread = 5
                }
            };

            // 3. JSON 직렬화 옵션 (CamelCase 변환 및 Null 무시)
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };

            string jsonContent = JsonSerializer.Serialize(requestData, jsonOptions);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            try
            {
                // 4. 요청 전송 (client는 외부에서 주입받거나 static으로 관리 권장)
                HttpResponseMessage response = await client.PostAsync(chatUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    string errorInfo = await response.Content.ReadAsStringAsync();
                    return $"[서버 오류 {response.StatusCode}] {errorInfo}";
                }

                string responseBody = await response.Content.ReadAsStringAsync();

                // 5. 응답 파싱
                using (JsonDocument doc = JsonDocument.Parse(responseBody))
                {
                    if (doc.RootElement.TryGetProperty("message", out JsonElement messageElement) &&
                        messageElement.TryGetProperty("content", out JsonElement contentElement))
                    {
                        return contentElement.GetString();
                    }
                    else
                    {
                        return $"[응답 형식 오류] 예상된 'message.content' 필드가 없습니다.\n{responseBody}";
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return "[시간 초과] 모델 응답이 너무 늦습니다. (Timeout 설정을 확인하세요)";
            }
            catch (HttpRequestException ex)
            {
                return $"[연결 실패] Ollama가 실행 중인지 확인하세요. (http://localhost:11434)\n{ex.Message}";
            }
            catch (Exception ex)
            {
                return $"[예외 발생] {ex.Message}";
            }
        }

        private async Task UnloadModel(string modelName)
        {
            var requestData = new { model = modelName, keep_alive = 0 };
            var content = new StringContent(JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json");
            await client.PostAsync(OLLAMA_URL, content);
        }

        private void btnOpenFile_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.InitialDirectory = @"E:\DevProject\SM48";
                openFileDialog.Multiselect = true;             // 멀티 선택 활성화
                openFileDialog.Title = "파일을 선택하세요";     // 창 제목
                openFileDialog.Filter = "모든 파일 (*.*)|*.*"; // 파일 필터

                // 2. 파일 선택 창 열기
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    // 3. 선택된 파일 경로들을 ListBox에 누적
                    foreach (string filePath in openFileDialog.FileNames)
                    {
                        // 중복된 경로가 없는 경우에만 추가 (선택 사항)
                        if (!lstFiles.Items.Contains(filePath))
                        {
                            lstFiles.Items.Add(filePath);
                        }
                    }
                }
            }

            UpdateFileCont();
        }

        private void btnRemoveFile_Click(object sender, EventArgs e)
        {
            // 1. 선택된 아이템이 있는지 확인
            if (lstFiles.SelectedItems.Count > 0)
            {
                // 2. 뒤에서부터 삭제해야 인덱스가 변하지 않음
                // SelectedIndices를 사용해 선택된 위치의 번호들을 가져옵니다.
                for (int i = lstFiles.SelectedIndices.Count - 1; i >= 0; i--)
                {
                    int indexToRemove = lstFiles.SelectedIndices[i];
                    lstFiles.Items.RemoveAt(indexToRemove);
                }

                UpdateFileCont();
            }
            else
            {
                MessageBox.Show("삭제할 파일을 리스트에서 선택해주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    Document doc = txtAttFile.Document;

                    if (File.Exists(filePath))
                    {
                        // 파일 내용 읽기
                        string fileContent = File.ReadAllText(filePath);

                        //// 3. 문서 끝에 파일명 구분선 추가 (선택 사항)
                        //DocumentRange headerRange = doc.AppendText($"\n--- File: {Path.GetFileName(filePath)} ---\n");

                        //// 구분선에 볼드체 서식 적용 예시
                        //CharacterProperties cp = doc.BeginUpdateCharacters(headerRange);
                        //cp.Bold = true;
                        //doc.EndUpdateCharacters(cp);

                        // 4. 본문 내용 누적
                        doc.AppendText(fileContent);
                        doc.AppendText("\n\n\n"); // 파일 간 간격을 위한 줄바꿈
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"오류 발생: {ex.Message}");
            }
            finally
            {
                // 5. 업데이트 종료 및 화면 갱신
                txtAttFile.EndUpdate();

                // 스크롤을 문서 끝으로 이동
                txtAttFile.ScrollToCaret();
            }
        }
    }
}
