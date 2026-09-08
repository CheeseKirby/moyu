'use strict';
const app = document.querySelector('.app'), pageArea = document.querySelector('.page-area');
let toastTimer;
function notify(text) { const toast = document.querySelector('.toast'); clearTimeout(toastTimer); toast.textContent = text; toast.hidden = false; toastTimer = setTimeout(() => { toast.hidden = true; }, 3200); }
function showPage(name) {
  document.querySelectorAll('.page').forEach(page => { page.hidden = page.id !== name; });
  document.querySelectorAll('[data-page]').forEach(button => { if (button.dataset.page === name) button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current'); });
  document.querySelector('#page-name').textContent = {task:'字幕任务',settings:'道具设置',library:'字幕库'}[name];
  app.classList.toggle('settings-active', name === 'settings');
  document.querySelector('#footer-settings').hidden = name !== 'settings';
  document.querySelector('#footer-task').hidden = name === 'settings';
  document.querySelector('#footer-right').hidden = name === 'settings';
  pageArea.scrollTop = 0;
}
document.querySelectorAll('[data-page]').forEach(button => button.addEventListener('click', () => showPage(button.dataset.page)));
document.querySelector('[data-goto-task]').addEventListener('click', () => showPage('task'));
document.querySelector('#language-link').addEventListener('click', () => {showPage('settings'); document.querySelector('#language').focus({preventScroll:true});});
document.querySelector('#language').addEventListener('change', event => {document.querySelector('#language-label').textContent = event.target.value;});
document.querySelector('#choose').addEventListener('click', () => document.querySelector('#file').click());
function selectFile(file) { if (!file) return; if (!/\.(mp4|mkv|mov|avi|wmv|flv|webm|m4v|ts|m2ts)$/i.test(file.name)) {notify('请选择视频文件。预览不会读取或上传文件内容。');return;} document.querySelector('#file-title').textContent = file.name; document.querySelector('#file-title').title = file.name; document.querySelector('#file-hint').textContent = '已选择 · 仅展示文件名，不读取或上传视频'; document.querySelector('#generate').disabled = false; }
document.querySelector('#file').addEventListener('change', event => selectFile(event.target.files[0]));
const drop = document.querySelector('#drop-area');
['dragenter','dragover'].forEach(type => drop.addEventListener(type, event => {event.preventDefault();drop.classList.add('dragging');}));
['dragleave','drop'].forEach(type => drop.addEventListener(type, event => {event.preventDefault();drop.classList.remove('dragging');}));
drop.addEventListener('drop', event => {if(event.dataTransfer.files.length !== 1) notify('请一次选择一个视频。');else selectFile(event.dataTransfer.files[0]);});
document.querySelector('#generate').addEventListener('click', () => notify('这是设计预览，不会启动字幕任务。'));
document.querySelectorAll('[data-window]').forEach(button => button.addEventListener('click', () => notify(button.dataset.window === 'close' ? '这里只展示关闭按钮；正式应用仍保留关闭到托盘。' : '这里只展示最小化按钮，不会最小化浏览器。')));
document.querySelector('#maximize').addEventListener('click', event => { const maximized = app.classList.toggle('expanded'); event.currentTarget.setAttribute('aria-label',maximized?'还原窗口':'最大化'); });
document.addEventListener('keydown', event => {if(event.key === 'Escape') {app.classList.remove('expanded');document.querySelector('#maximize').setAttribute('aria-label','最大化');}});
document.querySelector('#watcher-switch').addEventListener('click', event => {const button = event.currentTarget;button.setAttribute('aria-checked',button.getAttribute('aria-checked') !== 'true' ? 'true':'false');});
document.querySelector('#show-key').addEventListener('click', event => {const input = document.querySelector('#api-key');const show = input.type === 'password';input.type = show ? 'text':'password';event.currentTarget.textContent = show ? '隐藏':'显示';event.currentTarget.setAttribute('aria-label',show?'隐藏密钥':'显示密钥');});
document.querySelector('#save-settings').addEventListener('click', () => notify('已展示保存反馈。预览不会写入正式设置。'));
document.querySelector('#test-connection').addEventListener('click', () => notify('连接测试仅为操作示意，不会发出网络请求。'));
document.querySelector('#choose-directory').addEventListener('click', () => notify('目录选择仅作示意，不会修改归档位置。'));
document.querySelector('#refresh-library').addEventListener('click', () => notify('预览字幕库为空，没有读取你的真实归档。'));
