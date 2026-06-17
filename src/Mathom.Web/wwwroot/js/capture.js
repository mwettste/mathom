// Alpine components for the capture page. Loaded before alpine.min.js (and after
// offline.js) so the globals exist at init. Sending is routed through
// window.mathomOutbox so captures survive being offline.

function textCapture() {
  return {
    text: '',
    busy: false,
    status: '',
    async submit() {
      if (!this.text.trim()) { this.status = 'Type something first.'; return; }
      this.busy = true;
      const res = await window.mathomOutbox.send({
        id: crypto.randomUUID(),
        kind: 'text',
        text: this.text,
      });
      this.status = res.status;
      if (res.ok || res.queued) this.text = '';
      this.busy = false;
    },
  };
}

function voiceCapture() {
  return {
    recording: false,
    uploading: false,
    elapsed: 0,
    status: '',
    done: false,
    _rec: null,
    _chunks: [],
    _timer: null,
    fmt(s) {
      const m = Math.floor(s / 60);
      const ss = String(s % 60).padStart(2, '0');
      return `${m}:${ss}`;
    },
    async toggle() {
      if (this.uploading) return;
      if (this.recording) { this._rec.stop(); return; }
      let stream;
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      } catch {
        this.status = 'Microphone unavailable.';
        return;
      }
      this._chunks = [];
      this.done = false;
      this.status = '';
      this._rec = new MediaRecorder(stream);
      this._rec.ondataavailable = (e) => { if (e.data.size) this._chunks.push(e.data); };
      this._rec.onstop = async () => {
        clearInterval(this._timer);
        this.recording = false;
        stream.getTracks().forEach((t) => t.stop());
        await this.upload();
      };
      this._rec.start();
      this.recording = true;
      this.elapsed = 0;
      this._timer = setInterval(() => this.elapsed++, 1000);
    },
    async upload() {
      this.uploading = true;
      const type = this._rec.mimeType || 'audio/webm';
      const ext = type.includes('mp4') || type.includes('mpeg') ? 'm4a' : 'webm';
      const blob = new Blob(this._chunks, { type });
      const res = await window.mathomOutbox.send({
        id: crypto.randomUUID(),
        kind: 'voice',
        blob,
        filename: `note.${ext}`,
      });
      this.status = res.status;
      this.done = res.ok; // only show the "transcribing in the background" pulse when actually sent
      this.uploading = false;
    },
  };
}
