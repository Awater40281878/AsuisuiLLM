const question = uiv.getVar('!CMD_VAR1');
if (!question) throw new Error('缺少 PowerShell 傳入的問題');

uiv.goto('https://chatgpt.com/');

// 使用 ChatGPT 的可存取名稱定位輸入框，避開易變動的 CSS id。
const composer = uiv.getByRole('textbox', { name: '問問 ChatGPT' });
uiv.browser.click(composer);
uiv.browser.press('Control+A');
uiv.browser.type(question);

// 記錄送出前的訊息數，避免把舊訊息誤認為本次成功。
const previousUserMessageCount = uiv.$$('css=[data-message-author-role="user"]').length;

// ChatGPT 繁體中文介面中，藍色箭頭的可存取名稱是「傳送」。
const sendButton = uiv.getByRole('button', { name: '傳送' });
uiv.browser.click(sendButton);

const deadline = Date.now() + 20000;
let confirmed = false;
while (Date.now() < deadline) {
  const userMessages = uiv.$$('css=[data-message-author-role="user"]');
  const lastMessage = userMessages[userMessages.length - 1];
  if (userMessages.length > previousUserMessageCount &&
      lastMessage && lastMessage.text.includes(question)) {
    confirmed = true;
    break;
  }
  uiv.sleep(500);
}

if (!confirmed) {
  throw new Error('已點擊傳送，但未在對話中確認本次問題');
}

uiv.log('ChatGPT 已收到本次問題', 'green');

