# Unity 语音 AI（录音 → DeepSeek → 语音回复）— Android

在 Unity 手机 App 里实现：点按钮说话 → 系统语音识别成文字 → 调 DeepSeek API 得到回复 → 系统 TTS 朗读出来。
全部使用 **Android 系统自带** 的语音识别与语音合成，无需付费第三方服务（DeepSeek 按量计费，很便宜）。

## 文件结构

| 文件 | 作用 |
| --- | --- |
| Assets/Scripts/VoiceAI/AndroidSpeechRecognizer.cs | 封装 Android 系统语音识别 (SpeechRecognizer)，自带录音，返回中文文本 |
| Assets/Scripts/VoiceAI/AndroidTextToSpeech.cs | 封装 Android 系统文字转语音 (TextToSpeech)，直接扬声器播放 |
| Assets/Scripts/VoiceAI/DeepSeekClient.cs | DeepSeek Chat API 客户端（OpenAI 兼容格式，非流式） |
| Assets/Scripts/VoiceAI/VoiceAIController.cs | 总控组件：串起 识别→DeepSeek→TTS，含状态机、权限申请、UI 刷新 |
| Assets/Scripts/Editor/VoiceAISetup.cs | 编辑器菜单：一键生成演示 UI |
| Assets/Plugins/Android/AndroidManifest.xml | 申请 INTERNET / RECORD_AUDIO 权限 + Android 11 包可见性 |

## 使用步骤

1. **申请 DeepSeek Key**：到 platform.deepseek.com 创建 API Key（sk- 开头）。
2. **打开工程**：用 Unity 6000.3.x 打开本工程，打开 SampleScene。
3. **生成 UI**：菜单 **Tools → VoiceAI → 创建演示 UI**（或手动：场景里加一个空物体挂 VoiceAIController，再做一个 Button 绑定它的 ToggleListening()）。
4. **填 Key**：选中场景里的 VoiceAI_Canvas，在 Inspector 的 "DeepSeek 配置" 里粘贴 API Key（可选：改 systemPrompt、model）。
5. **构建 Android**：
   - Unity Hub 确认已安装 **Android Build Support**（含 SDK/NDK/OpenJDK）。
   - File → Build Settings → 切到 Android，Add Open Scenes 加入当前场景。
   - Player Settings → Other Settings：Scripting Backend 选 **IL2CPP**，Target Architectures 勾 **ARM64**，Minimum API Level 建议 **API 23+**。
   - 连接手机（开启开发者模式/USB 调试）→ Build And Run。
6. **真机测试**：首次点按钮会弹麦克风权限，允许后按住/点击说话，说一句话，等待 DeepSeek 回复后手机会朗读出来。

## 交互方式

- 默认 **点击切换**：点一下开始听，再点一下结束。
- 需要 **按住说话**：选中 VoiceAI_Canvas，Inspector 里把 "Hold To Talk" 打勾（此时请把按钮的 OnClick 事件清空，避免和按住冲突）。

## 常见问题

- **"缺少中文语音包"**：TTS 需要中文语音数据。设置 → 系统 → 语言与输入法 → 文字转语音 → 安装中文语音（推荐 Google TTS）。
- **"识别服务忙"**：连续快速点击导致，稍等 1~2 秒再试。
- **"网络错误/超时"**：确认手机能访问 api.deepseek.com（国内网络可能需要代理；也可以把 apiUrl 换成 DeepSeek 的国内中转/代理地址）。
- **模拟器上无法识别**：SpeechRecognizer 依赖 Google 语音服务，很多模拟器不支持，请用真机。
- **编辑器里点按钮提示"仅支持 Android 真机"**：正常，系统识别/TTS 只能真机跑；DeepSeek 环节可在手机上观察。

## 重要提醒

- **API Key 安全**：当前 Key 直接打包进 APK，可被反编译提取。个人自用没问题；正式上线请改为"服务器中转"（App 调你自己的后端，后端保存 Key 再转发 DeepSeek）。
- **识别/朗读是系统级的**：识别准确率和音色取决于手机（Google 语音服务的质量）。想要更准/更好听的语音，可以把 STT 换成 Whisper API、TTS 换成 Azure/讯飞等云端服务（DeepSeekClient 的写法可以直接套用——同样都是 UnityWebRequest 发 JSON）。

## 后续扩展方向

- 流式输出：DeepSeek 支持 SSE 流式，可以边生成边显示。
- 多轮对话：把 messages 历史保存下来连续聊天。
- 云端 STT/TTS：用麦克风录 WAV（Microphone 类），POST 给 Whisper / Azure 等接口，返回的音频用 AudioSource 播放。
