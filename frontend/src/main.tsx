import React from 'react';
import ReactDOM from 'react-dom/client';
import { initFaro } from './faro';
import { App } from './App';

// Initialize Faro before the app renders so it captures the full session.
initFaro();

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
