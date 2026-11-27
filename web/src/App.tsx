import { Header } from './components/Header';
import { Hero } from './components/Hero';
import { RunStory } from './components/RunStory';
import { Deliverables } from './components/Deliverables';
import { FitOut } from './components/FitOut';
import { Principles } from './components/Principles';
import { Setup } from './components/Setup';
import { Status } from './components/Status';
import { EarlyAccess } from './components/EarlyAccess';
import { TitleBlock } from './components/TitleBlock';

export default function App() {
  return (
    <>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <Header />
      <div className="frame">
        <main id="main">
          <Hero />
          <RunStory />
          <Deliverables />
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
