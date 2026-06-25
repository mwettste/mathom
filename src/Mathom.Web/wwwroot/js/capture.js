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
      if (res.ok || res.queued) {
        this.text = '';
        if (window.toast) toast(res.queued ? 'Saved offline — will sync when online' : 'Captured — processing in the background', res.queued ? 'info' : 'success');
      }
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
      if (window.toast && (res.ok || res.queued)) toast(res.queued ? 'Saved offline — will sync when online' : 'Captured — transcribing in the background', res.queued ? 'info' : 'success');
      this.uploading = false;
    },
  };
}

function photoCapture() {
  return {
    bag: new DataTransfer(),
    previews: [],
    count: 0,
    photoContext: '',
    busy: false,
    status: '',
    done: false,
    pick(e) {
      const incoming = Array.from(e.target.files || []);
      let capped = false;
      for (const f of incoming) {
        if (this.bag.files.length >= 8) { this.status = 'At most 8 images.'; capped = true; break; }
        this.bag.items.add(f);
        this.previews.push({ url: URL.createObjectURL(f) });
      }
      this.count = this.bag.files.length;
      this.done = false;
      if (!capped) this.status = '';
      e.target.value = '';
    },
    remove(i) {
      URL.revokeObjectURL(this.previews[i].url);
      this.previews.splice(i, 1);
      const dt = new DataTransfer();
      for (let j = 0; j < this.bag.files.length; j++) if (j !== i) dt.items.add(this.bag.files[j]);
      this.bag = dt;
      this.count = this.bag.files.length;
    },
    reset() {
      for (const p of this.previews) URL.revokeObjectURL(p.url);
      this.previews = [];
      this.bag = new DataTransfer();
      this.count = 0;
      this.photoContext = '';
    },
    async submit() {
      if (!this.count) { this.status = 'Choose a photo first.'; return; }
      if (this.count > 8) { this.status = 'At most 8 images.'; return; }
      this.busy = true;
      try {
        const form = new FormData();
        for (const f of this.bag.files) form.append('images', f, f.name || 'photo.jpg');
        if (this.photoContext.trim()) form.append('context', this.photoContext.trim());
        form.append('idempotencyKey', crypto.randomUUID());
        const res = await fetch('/capture/photo', { method: 'POST', body: form });
        if (res.ok) {
          this.status = 'Sent.';
          this.done = true;
          this.reset();
        } else {
          let msg = 'Upload failed.';
          try { msg = (await res.json()).error || msg; } catch {}
          this.status = msg;
        }
      } catch {
        this.status = 'Upload failed.';
      } finally {
        this.busy = false;
      }
    },
  };
}
