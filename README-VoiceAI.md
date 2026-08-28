# Unity 语音 AI 助手（说话 → 识别 → DeepSeek → 你的声音回复）

一个 Unity 6 (6000.3.x) Android 语音交互应用：点击按钮说话 → 云端语音识别成文字 → 调用 DeepSeek 大模型对话 → 用**你自己克隆的声音**朗读回复。

## 架构总览

```
说"何夕月" → [⓪ 唤醒词检测 sherpa-onnx] → 自动开始录音（无需点击按钮）
你说话 → [① Microphone 录音] → WAV二进制 → [② 云端识别 STT] → 文字
        → [③ DeepSeek 大模型] → 回复文字 → [④ 云端语音合成 TTS] → 音频 → 扬声器播放
```

| # | 环节 | 方案 | 接口 | 费用 |
| --- | --- | --- | --- | --- |
| ① | 录音 | Unity Microphone 类（系统级） | 本地 | 免费 |
| ② | 语音转文字 STT | 硅基流动（SenseVoice） | `POST api.siliconflow.cn/v1/audio/transcriptions` | 免费额度，按量极低 |
| ③ | 对话 | DeepSeek | `POST api.deepseek.com/chat/completions` | 极低，按 token |
| ④ | 文字转语音 TTS | **系统 TTS（默认，免费）**，MiniMax 云端可选 | 本地 / `POST api.minimaxi.com/v1/t2a_v2` | 系统免费；MiniMax 按字符 |

> 为什么不用系统语音识别/合成？国行安卓机（如一加 13T）没有系统语音识别服务（RecognitionService 为空），且系统 TTS 音色固定无法克隆；云端方案跨机型通用、音色可定制。

## 文件结构

| 文件 | 作用 |
| --- | --- |
| `Assets/Scripts/VoiceAI/VoiceAIController.cs` | 总控：唤醒词→录音→识别→DeepSeek→TTS，状态机、权限、按钮自动绑定、静音自动停止 |
| `Assets/Scripts/VoiceAI/WakeWordDetector.cs` | 唤醒词检测：常驻麦克风 + sherpa-onnx 关键词检测，命中"何夕月"触发录音 |
| `Assets/Scripts/VoiceAI/SherpaOnnxNative.cs` | sherpa-onnx C API 的 P/Invoke 封装（结构体布局与 c-api.h 对齐） |
| `Assets/Scripts/VoiceAI/EdgeGlowEffect.cs` | 屏幕四边光效，随状态变色（纯观感） |
| `Assets/StreamingAssets/KwsModel/` | 唤醒词模型：流式 zipformer 拼音 transducer（encoder/decoder/joiner + tokens + keywords） |
| `Assets/Plugins/Android/arm64-v8a/` | sherpa-onnx / onnxruntime 原生库 |
| `Assets/Scripts/VoiceAI/WavUtility.cs` | AudioClip → WAV（16kHz 单声道 PCM16） |
| `Assets/Scripts/VoiceAI/CloudSttClient.cs` | 云端识别客户端（OpenAI 兼容 /audio/transcriptions） |
| `Assets/Scripts/VoiceAI/DeepSeekClient.cs` | DeepSeek Chat API 客户端 |
| `Assets/Scripts/VoiceAI/CloudTtsClient.cs` | MiniMax T2A v2 客户端（pcm+hex 方案，实测验证） |
| `Assets/Scripts/VoiceAI/AndroidTextToSpeech.cs` | （备用）系统 TTS，`useCloudTts=false` 时启用 |
| `Assets/Scripts/Editor/VoiceAISetup.cs` | 菜单一键生成演示 UI |
| `Assets/Plugins/Android/AndroidManifest.xml` | 权限 + 启动 Activity + 包可见性 |

## 快速开始

### 1. 申请三个 Key

| Key | 平台 | 入口 |
| --- | --- | --- |
| DeepSeek | platform.deepseek.com | API Keys → 创建 |
| STT | cloud.siliconflow.cn | 控制台 → API 密钥 |
| TTS | platform.minimaxi.com | 基本信息 → 接口密钥（需实名认证） |

### 2. 配置 Inspector

菜单 **Tools → VoiceAI → 创建演示 UI**（或手动挂 `VoiceAIController` 到 Canvas）。选中 `VoiceAI_Canvas` 填写：

- **DeepSeek 配置**：`apiKey`
- **语音识别(STT) 配置**：`apiKey`（模型默认 FunAudioLLM/SenseVoiceSmall）
- **语音合成(TTS) 配置**：`apiKey` + `voiceId`
  - **默认系统 TTS（免费）**：`useCloudTts` 不勾选即可，手机设置→文字转语音 可换系统女声音色
  - 要 MiniMax 云端时勾选 `useCloudTts`（需余额；克隆音色有 7 天保活机制）
  - 免费女声（MiniMax 官方音色，无 7 天删除问题）：`female-shaonv`（少女）、`female-yujie`（御姐）、`female-chengshu`（成熟女性）、`female-tianmei`（甜美女性）
  - 男声：`male-qn-qingse`（青涩青年）等；或自己的克隆音色（如 `TSY_voice01`）

### 3. 构建运行

Unity 6000.3.x → Build Settings 选 Android（IL2CPP + ARM64，最低 API 23+）→ 真机 Build And Run。
点按钮说话 → 说完了静音 2.5 秒自动结束 → 识别 → DeepSeek → 朗读。

## 定制你的声音（MiniMax 声音复刻）

MiniMax 复刻是 API 流程（控制台无网页按钮），两个接口即可：

```bash
# 1) 上传样本（10秒~5分钟，mp3/m4a/wav，≤20MB，普通话清晰无噪音）
curl -X POST https://api.minimaxi.com/v1/files/upload \
  -H "Authorization: Bearer <MINIMAX_KEY>" \
  -F "purpose=voice_clone" -F "file=@你的录音.mp3"
# → 返回 file_id

# 2) 克隆（voice_id 规则：8-256字符，字母开头，仅字母数字-_，末尾不能是-_）
curl -X POST https://api.minimaxi.com/v1/voice_clone \
  -H "Authorization: Bearer <MINIMAX_KEY>" \
  -H "Content-Type: application/json" \
  -d '{"file_id":<上一步的file_id>,"voice_id":"MyVoice01","text":"试听文本","model":"speech-2.8-hd"}'
# → success，得到 demo_audio 试听链接；voice_id 就是你自己起的名字
```

然后把 `voice_id` 填进 Inspector「语音合成(TTS) 配置」，重新打包即可。**换声音只需改 voice_id。**

⚠️ 注意：
- 复刻需先完成 MiniMax 实名认证
- **克隆的音色 7 天未使用会被系统删除**（正常使用 App 即可保留）
- 克隆他人声音需获得授权

## 语音识别方言说明

**识别引擎开关**：Inspector「语音识别(STT) 配置」→ `provider`：
- `0`（默认）：OpenAI 兼容接口（硅基流动 SenseVoiceSmall）——支持普通话/粤语/英语/日语/韩语
- `1`：火山引擎录音文件识别（需 volcAppId / volcAccessToken + 授权开通）
- `2`：**讯飞语音听写（推荐陕西话）**——官方支持 **23 种方言含陕西话**

**讯飞「方言识别大模型」（陕西话主引擎，已真机端到端验证）**：
1. 注册 xfyun.cn → 控制台创建应用 → 开通「方言识别大模型」
2. 控制台复制 **APPID / APIKey / APISecret**
3. Inspector：provider=2、iflyAppId、iflyApiKey、iflyApiSecret、iflyDomain=slm
   - iflyAccent 默认 **mulacc**（多口音自动识别，官方 23 种方言含陕西话）
4. 重新打包即可

**免费备用引擎（降级）**：主识别失败自动切换：
- 开通「语音听写（流式版）」→ 在 Inspector 的「识别备用引擎(STT 降级)」填一套 SttSettings：
  provider=2、同样的 APPID/APIKey/APISecret、**iflyDomain=iat**（流式听写协议，免费额度）
- 主引擎挂了/额度用完时，App 自动用流式听写兜底

**性能**：讯飞音频按 8 倍实时速率上传（2560B@10ms 实测安全），10 秒录音约 1.3 秒传完

## 流式回复与文字显示

- DeepSeek 回复采用 **SSE 流式**：文字边生成边逐个显示（不用等完整回复）
- 所有 Text 组件已做显示优化：自动换行、溢出不截断、高度随内容自适应增长（运行时自动配置，旧场景同样生效）

## 保活与自愈（防克隆音色被删除）

MiniMax 规则：**克隆音色连续 7 天未合成就会被删除**。App 内置两道防线（默认开启，Inspector「保活与自愈」可关）：

1. **自动保活**：打开 App 时若距上次合成超过 `keepAliveDays`（默认 5 天），后台静默合成一次极短文本（成本约几厘钱），刷新 7 天计时——**客户只要打开过 App，声音就永远不会丢**。
2. **自愈重克隆**：若仍因超期被删（TTS 报 voice 相关错误），App 自动读取 `Assets/StreamingAssets/VoiceSample/clone_sample.mp3`（内置样本）→ 重新上传 → 重新克隆（复用同一 voice_id）→ 自动重试合成，客户无感知。

> 使用前请把声音样本放到 `Assets/StreamingAssets/VoiceSample/clone_sample.mp3`（10秒~5分钟，mp3/wav/m4a），换样本后重新 Build。

## 交互方式

- 默认**唤醒词**：说"何夕月"自动开始录音，说完静音 2.5 秒自动结束 → 识别 → DeepSeek → 朗读（无需点击按钮）
- **手动按钮（备用）**：场景里的按钮仍可点击开始/结束（说"何夕月"也能打断朗读开始新对话）
- **按住说话**：Inspector 勾选 `holdToTalk`（与唤醒词可共存）
- 状态机：空闲（监听唤醒词）→ 录音中 → 思考中（识别+大模型）→ 朗读中 → 空闲

## 唤醒词

- 默认唤醒词 **"何夕月"**（`StreamingAssets/KwsModel/keywords.txt` 同时配置了"何夕月"与"你好何夕月"，两个都能触发）
- 实现：本地 **sherpa-onnx 关键词检测**（流式 zipformer 拼音 transducer 模型），全程离线、零延迟、不消耗云端额度
- 首次运行会自动把模型从 StreamingAssets 复制到 `persistentDataPath/KwsModel`（原生库需要磁盘真实路径）
- Inspector「唤醒词」配置：
  - `enableWakeWord`（默认开）：false 则退回纯按钮模式
  - `wakeWordText`（默认"何夕月"）：仅用于界面提示，实际匹配由 keywords.txt 决定
  - `wakeOnlyWhenIdle`（默认开）：true=思考/朗读中忽略唤醒词，false=随时可打断

## 常见问题

| 现象 | 处理 |
| --- | --- |
| 提示"仅支持 Android 真机" | 系统识别/TTS 需真机；云端方案在编辑器也可测（需电脑麦克风+Key） |
| 识别失败 HTTP 401/403 | STT 的 Key 填错 |
| DeepSeek 401 | Key 失效，去平台重新生成 |
| TTS 合成失败 status_code≠0 | MiniMax Key/实名认证/voice_id 问题 |
| 没有识别到内容 | 声音太小或静音阈值提前触发，调大 Silence Auto Stop Seconds |
| 说"何夕月"没反应 | 确认已授权麦克风；首次启动联网等待模型复制完成；看 Logcat 是否有 `[WakeWord]` 日志 |
| 按钮无反应 | 确认 EventSystem 存在（组件会自动创建）；检查 Console 日志 |

## 安全提醒

- **三个 API Key 都序列化在场景文件里**，随 git 入库。仓库勿公开；公开前重新生成所有 Key
- 正式发布请改为服务器中转（Key 放服务端），避免被反编译提取
- 本工程仅用于学习/个人使用

## 扩展方向

- 多轮对话记忆、DeepSeek 流式输出（SSE）
- 换成更自然的云端 TTS 音色（MiniMax 官方音色列表见平台文档「系统音色列表」）
- 声纹唤醒、连续对话
