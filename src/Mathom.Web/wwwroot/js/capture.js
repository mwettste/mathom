// Alpine components for the capture page. Loaded before alpine.min.js so the
// x-data="textCapture()" / x-data="voiceCapture()" globals exist at init.

function textCapture() {
  return {
    text: '',
    busy: false,
    status: '',
    async submit() {
      if (!this.text.trim()) { this.status = 'Type something first.'; return; }
      this.busy = true;
      try {
        const res = await fetch('/capture', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ text: this.text, idempotencyKey: crypto.randomUUID() }),
        });
        this.status = res.ok ? 'Filed ✓' : 'Something went wrong — try again.';
        if (res.ok) this.text = '';
      } catch {
        this.status = 'Network error — try again.';
      } finally {
        this.busy = false;
      }
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
      try {
        const type = this._rec.mimeType || 'audio/webm';
        const ext = type.includes('mp4') || type.includes('mpeg') ? 'm4a' : 'webm';
        const blob = new Blob(this._chunks, { type });
        const form = new FormData();
        form.append('audio', blob, `note.${ext}`);
        form.append('idempotencyKey', crypto.randomUUID());
        this.status = 'Uploading…';
        const res = await fetch('/capture/voice', { method: 'POST', body: form });
        this.status = res.ok ? 'Filed ✓' : 'Something went wrong — try again.';
        this.done = res.ok;
      } catch {
        this.status = 'Network error — try again.';
      } finally {
        this.uploading = false;
      }
    },
  };
}
