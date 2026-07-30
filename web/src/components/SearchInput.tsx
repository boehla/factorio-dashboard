// Geteiltes Suchfeld fuer Maschinen-Panel und Item-Graph, damit Optik und
// Verhalten (Escape leert, x-Button) an beiden Stellen identisch sind.
export function SearchInput({ value, onChange, placeholder = "suchen…", className, onEnter }:
  { value: string; onChange: (v: string) => void; placeholder?: string;
    className?: string; onEnter?: () => void }) {
  return (
    <div className={`relative inline-flex items-center ${className ?? ""}`}>
      <input
        type="text"
        value={value}
        placeholder={placeholder}
        onChange={e => onChange(e.target.value)}
        onKeyDown={e => {
          if(e.key === "Escape") { onChange(""); (e.target as HTMLInputElement).blur(); }
          else if(e.key === "Enter") onEnter?.();
        }}
        className="w-full text-xs bg-black/30 border border-panelborder rounded pl-2 pr-5 py-0.5
          outline-none focus:border-gray-500 placeholder:text-gray-600"
      />
      {value && (
        <button onClick={() => onChange("")}
          className="absolute right-1 text-gray-500 hover:text-gray-300 leading-none"
          title="Suche leeren">&times;</button>
      )}
    </div>
  );
}
