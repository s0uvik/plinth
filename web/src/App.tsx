import { Header } from './components/Header';
import { Hero } from './components/Hero';
import { Register } from './components/Register';
import { RunStory } from './components/RunStory';
import { Talk } from './components/Talk';
import { FitOut } from './components/FitOut';
import { Principles } from './components/Principles';
import { Setup } from './components/Setup';
import { Status } from './components/Status';
import { EarlyAccess } from './components/EarlyAccess';
import { TitleBlock } from './components/TitleBlock';
import { useSmoothScroll } from './lib/smoothScroll';

export default function App() {
  useSmoothScroll();

  return (
    <>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <Header />
      <div className="frame">
        <main id="main">
          <Hero />
          <Register />
          <RunStory />
          <Talk />
          <FitOut />
          <Principles />
          <Setup />
          <Status />
          <EarlyAccess />
        </main>
        <TitleBlock />
      </div>
    </>
  );
}
