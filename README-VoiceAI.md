# Unity 语音 AI（录音 → 云端识别 → DeepSeek → 语音回复）— Android

在 Unity 手机 App 里实现：点按钮说话 → 录音 → 云端语音识别成文字 → 调 DeepSeek API 得到回复 → 系统 TTS 朗读出来。

## 方案总览（重要）

| 环节 | 方案 | 说明 |
| --- | --- | --- |
| 录音 | Unity Microphone 类 | 系统级，无需额外 SDK |
| 语音转文字 (STT) | **云端 OpenAI 兼容接口** | 国行安卓机一般没有系统语音识别服务（本机一加13T实测 RecognitionService 为 0），系统 SpeechRecognizer 不可用；云端方案跨机型通用。默认配硅基流动（国内直连、免费额度、中文强） |
| 对话 | DeepSeek Chat API | 官方 OpenAI 兼容端点 |
| 文字转语音 (TTS) | Android 系统 TextToSpeech | 国行机有 Oplus 引擎，实测可用，免费 |

> 旧文件 AndroidSpeechRecognizer.cs（系统识别封装）保留但已不参与流程：如果以后换到装有 Google 语音服务的手机，可作备用。

## 文件结构

| 文件 | 作用 |
| --- | --- |
| Assets/Scripts/VoiceAI/VoiceAIController.cs | 总控：录音→识别→DeepSeek→TTS，状态机/权限/自动绑定按钮/静音自动停止 |
| Assets/Scripts/VoiceAI/WavUtility.cs | AudioClip → WAV（16kHz 单声道 PCM16） |
| Assets/Scripts/VoiceAI/CloudSttClient.cs | 云端语音识别客户端（OpenAI 兼容 /audio/transcriptions） |
| Assets/Scripts/VoiceAI/DeepSeekClient.cs | DeepSeek Chat API 客户端 |
| Assets/Scripts/VoiceAI/AndroidTextToSpeech.cs | 系统 TTS 封装（扬声器直接朗读） |
| Assets/Scripts/VoiceAI/AndroidSpeechRecognizer.cs | （备用）系统语音识别封装，需要设备有语音识别服务 |
| Assets/Scripts/Editor/VoiceAISetup.cs | 菜单一键生成演示 UI |
| Assets/Plugins/Android/AndroidManifest.xml | INTERNET / RECORD_AUDIO 权限 + 启动 Activity + 包可见性 |

## 使用步骤

1. **申请两个 Key**：
   - DeepSeek：platform.deepseek.com（sk- 开头）
   - STT：到 siliconflow.cn 注册 → 控制台创建 API Key（免费额度够用很久）。
     也可以换成其他 OpenAI 兼容识别服务（OpenAI Whisper 等）：改「语音识别(STT) 配置」里的接口地址/模型/Key 即可。
2. **打开工程**：用 Unity 6000.3.x 打开，打开 SampleScene。
3. **生成 UI**：菜单 Tools → VoiceAI → 创建演示 UI（已存在则跳过；按钮不需要手动绑定事件，总控组件会自动绑定）。
4. **填配置**：选中 VoiceAI_Canvas，在 Inspector 填写：
   - 「DeepSeek 配置」→ apiKey
   - 「语音识别(STT) 配置」→ apiKey（硅基流动）
5. **构建 Android**：IL2CPP + ARM64，最低 API 23+，真机 Build And Run。
6. **使用**：点按钮开始录音（状态文字显示秒数）→ 说完了再点一下（或静音 2.5 秒自动结束）→ 云端识别 → DeepSeek 回复 → 手机朗读。

## 交互方式

- 默认**点击切换**：点一下开始录音，再说完了点一下结束。
- **按住说话**：Inspector 把 "Hold To Talk" 打勾（总控组件只会在非按住模式下自动绑定按钮）。

## 常见问题

- **"语音识别请求失败(HTTP 401/403)"**：STT 的 API Key 填错或没填。
- **"语音识别请求失败(HTTP 4xx/5xx)"**：检查手机网络；模型名是否正确（硅基流动是 FunAudioLLM/SenseVoiceSmall）。
- **"没有识别到内容"**：离麦克风远/声音小，或静音阈值太灵敏提前结束了，可调大 "Silence Auto Stop Seconds"。
- **"TTS 尚未就绪"**：手机缺少中文语音包，到 设置→系统→语言与输入法→文字转语音 安装（国行机一般自带 Oplus 引擎）。
- **编辑器里测试**：可以走完 录音→识别→DeepSeek（需要电脑麦克风+Key）；TTS 朗读仅在 Android 真机生效，编辑器里会在 Console 打印回复文本。
- **API Key 安全**：Key 打包进 APK 可被反编译提取，正式发布请改为服务器中转。

## 换 STT 服务商

SttSettings 三个字段（Inspector 可改）：
- apiUrl：例如 OpenAI 用 https://api.openai.com/v1/audio/transcriptions
- model：OpenAI 用 whisper-1；硅基流动用 FunAudioLLM/SenseVoiceSmall
- apiKey：对应平台的 Key

## 后续扩展

- 流式输出（DeepSeek SSE）、多轮对话记忆
- 换更自然的云端 TTS（Azure/火山/OpenAI），返回音频用 AudioSource 播放
