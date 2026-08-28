---
title: "글감 메모 (발행 안 됨 — _drafts 폴더는 사이트에 노출되지 않습니다)"
---

# 블로그 글감

1. lifetime annotation도 borrow checker도 없이 메모리 안전을 사는 법 (1번+3번 통합)
    - 핵심 논증: readers-XOR-writer가 막는 건 데이터 레이스 → 데이터 레이스는 스레드가
      둘 이상이어야 생김 → 단일 스레드의 유일한 위험은 stale address (moved/freed) →
      그건 소유권 + 주소 안정성 규칙이 이미 막는다
    - lifetime annotation의 장점
        1. code가 flexible해진다
    - lifetime annotation의 문제점
        1. 보수적 근사라서 실제로는 멀쩡한 프로그램도 거부함 (거짓 양성)
            - 대표 예시: 같은 array의 서로 다른 원소에 대한 &mut 두 개 — `&mut v[0]` +
              `&mut v[1]`은 인덱스가 상수라 disjoint가 자명한데도 거부 (borrow를 원소가
              아니라 컨테이너 단위로 추적). 인용 가능한 자백: stdlib이 이걸 우회하려고
              split_at_mut / get_disjoint_mut을 추가함
            - 보조 예시: NLL problem case #3 (한 분기에서 반환된 borrow가 다른 분기까지
              오염) — Rust 팀 스스로 "sound한데 거부됨" 리스트에 올려둠 (Polonius가 고칠 대상)
            - 그 결과 (idx 얘기는 여기): 거부당한 설계(그래프 back-edge, self-referential,
              ECS)의 표준 탈출구 = 참조 대신 usize 인덱스 저장 (slotmap/petgraph/ECS 크레이트
              전부 이 패턴). 아이러니: 인덱스 = 검사기가 못 보는 참조 — 원소 제거/재배치 시
              소리 없이 dangling, 컴파일 에러도 panic 보장도 없음. 복잡성은 남고 안전은 떠남
            - RF의 답: 진짜 참조(Hijacked/RC)를 그냥 들게 하되, 검사 가능한 만큼 단순한
              규칙으로 — 거짓 양성 줄이려고 검사 자체를 포기하는 게 아님
        2. 검증기 자체에 알려진 soundness 구멍 — cve-rs가 시연하는 rust#25860은
           2015년부터 열려 있음; safe Rust만으로 use-after-free 가능 (거짓 음성)
           (주의: "설계가 틀렸다"가 아니라 "구현에 10년 묵은 구멍"으로 정확히 쓰고
           이슈 번호를 인용할 것 — "알려진 버그일 뿐, 설계는 건전하다"는 반박을 선제 차단)
        3. 2026년 시대 흐름 (AI 코딩이 활발한 시대)에 맞지 않는 과도한 복잡성
           (주의: 한 문장 양념으로만 — 기둥 논거로 쓰면 역공 빌미)
    - 대신 RazorForge가 하는 것: 스코프에 묶인 빌림 (반환/저장 불가), `steal` 명시 이전,
      탈출하는 참조는 Hijacked/RC
    - "거절"이 아니라 "유예": readers-XOR-writer는 멀티스레드 티어(Consulting/Amending)에서
      돌아옴 — 멀티스레드에서는 필요함을 강조
2. 굳이 키워드를 전통적인 이름으로 쓰지 않고 다른 이름을 쓰는 이유
    - 자연어 친화적인 키워드를 고집하게 됨
    - 테제: "특이한 이름을 골랐다"가 아니라 "전통 키워드는 거짓 주장을 하고, 나는 참인
      말만 하는 키워드를 원했다" — 실패 표시(!), 이전 표시(steal), 오버플로 선택과 같은
      원칙을 어휘에 적용한 것
    - 논란일 키워드: record/entity/routine
        - struct → record: struct는 의미가 아니라 레이아웃 얘기고 그마저 언어마다 다름
          (C=POD, C++=public 기본 class+vtable 가능, Rust=아무거나, Swift=값 타입).
          record는 Pascal/Ada/DB 계보로 "필드 그 자체인 값" (내용 비교, 복사, 정체성 없음).
          선례: C#도 2020년에 값 의미론을 신호하려고 record 키워드를 추가함
        - class → entity: class는 어원부터 분류 체계의 범주 — 상속 없는 구조물에 쓰는 게
          오히려 거짓 주장. entity = 정체성 + 생명주기 (단일 소유, 참조, 정해진 teardown).
          선례: DDD의 entity vs value object ("정체성으로 정의" vs "속성으로 정의")가
          entity/record 구분과 정확히 일치 + 게임 쪽 ECS도 같은 용법 (타깃 독자에게 친숙)
        - function → routine: 함수는 입력→출력 사상일 뿐 — 출력하고 변이하고 실패하는 걸
          function이라 부르는 게 업계가 물려받은 거짓말. Fortran SUBROUTINE / Pascal·Ada의
          procedure-function 구분이 원래 전통이고, 직접 선례는 Eiffel의 routine (계약에 의한
          설계 언어가 거짓 단어를 거부한 건 우연이 아님). 결정타: routine parse!는 두 번
          정직하고 "throws하는 function"은 두 번 거짓
    - 선제 방어: "익숙함의 가치 / 새 이름 학습 비용" — 답: 사전적 의미 그대로의 일상
      영단어라 학습 비용은 분 단위; 거짓 이름의 오교육 비용은 영구적 (class를 보면 모두가
      상속을 찾으러 감; entity를 보고 RF가 안 하는 걸 기대하는 사람은 없음).
      일회성 소액 vs 복리 혼란
3. Text가 codepoint collection인 이유
    - UTF8은 기본적으로 전송 포맷에 가깝다는 게 내 생각
    - 가변 문자열이 주는 불편함이 있음
    - 메모리 4배 낭비: 과연 진짜로 valid한가?
        - 1000만자를 ascii로 한다 한들 30MB 정도의 차이만 남 (그리고 한국어는 UTF-8이
          이미 3바이트/음절이라 4:3 비율밖에 안 됨 — 표로 보여줄 것)
        - 사실 그리고 긴 텍스트를 다룰 적엔 고정된 메모리 주소가 훨씬 도움이 됨 (배열적 알고리즘을 그대로 적용 가능)
          (표현 주의: "고정 폭(stride) → O(1) random access"로 쓸 것 — 주소 안정성과는 다른 성질)
        - 선제 방어 필수: 진짜 비용은 용량이 아니라 메모리 대역폭/캐시 (40MB 스캔 = 10MB의 4배).
          답: 기가바이트급 스캔은 전송 포맷 레벨의 일 → `Bytes`의 영역. Text = 의미, Bytes = 처리량.
          부정하지 말고 인정 후 분업으로 답할 것
        - 사는 것: 바이트 오프셋 슬라이스가 글자를 못 쪼갬 — Rust `&s[0..1]`은 한국어 문자열에서
          런타임 패닉; RazorForge에선 그 버그가 표현 자체가 불가능. `text[i]` = 항상 온전한
          codepoint, O(1). length도 단위가 일관됨 (UTF-8 = 바이트 수, UTF-16 = code unit 수)
        - 선례: Python. `str` 의미론이 정확히 codepoint 배열이고 CPython(PEP 393)은 Latin-1
          밖 문자가 하나라도 있으면 4바이트/char로 저장 — 수억 명이 이미 이 비용을 내면서
          Python 문자열을 "좋다"고 평가함
    - 단점: grapheme cluster
        - codepoint ≠ 사용자가 인지하는 글자: 👨‍👩‍👧 같은 ZWJ 조합 emoji, 결합 문자,
          풀어쓴(NFD) 한글 jamo
        - 핵심 반론: grapheme은 인코딩과 직교한 Unicode 테이블 문제 — UTF-8도 못 풀고 오히려
          더 나쁨. UTF-32 = O(1) codepoint + 라이브러리 grapheme / UTF-8 = O(1)인 게 아예 없음
            + 라이브러리 grapheme. 어떤 표현도 O(1) grapheme은 못 줌
        - 한국어 독자 보너스: 사실상 모든 한국어 텍스트인 NFC에선 1음절 = 1 codepoint라
          codepoint ≈ 글자
        - framing: "단점"이 아니라 "비판자 포함 아무도 공짜로 못 얻는 것"으로 쓸 것
4. SortedSet/SortedList/SortedDict에 get_by_rank 구현을 도입한 이유
    - 훅은 기능이 아니라 공백: order statistics ("지금 k번째로 작은 값" / "이 키의 순위")는
      주류 stdlib이 거의 다 못 함 — C++ std::set 불가(비표준 GNU PBDS order_of_key 필요),
      Rust BTreeSet 불가, Java TreeMap 불가, Python은 서드파티 sortedcontainers.
      실전 문제(실시간 중앙값/리더보드 순위/백분위)로 열고 타언어 우회로 보여준 뒤 RF 한 줄
    - 이름이 왜 BTreeSet/BTreeList/BTreeMap이 아닌가? → 구현이 아니라 계약으로 명명.
      정렬 순서가 약속이고 트리는 현재 구현 — 구현을 바꿔도 SortedSet은 참, BTreeSet은
      거짓말 또는 파괴적 개명. 2번 글(키워드 정직성)과 교차 링크
    - 왜 [] 연산자가 아니라 이름 있는 메서드인가 (이 글의 가장 독창적인 부분):
      []의 공통 의미 = "저장할 때 쓴 식별자로 접근" — 시퀀스는 위치(Indexable, U64),
      맵은 키(KeyFindable — Dict도 SortedDict도 키로 []를 씀; O(1) 신호 아님, SortedDict
      키 []는 O(log n)). rank는 식별자가 아니라 현재 내용물의 파생 속성: 아무도 원소를
      "rank 3에" 넣지 않았고, 더 작은 원소가 드나들 때마다 rank 3이 가리키는 원소가
      바뀜. []는 "이 주소에 있는 것"을 묻고 rank는 "지금 순서"를 묻는 질의 → 이름을 줌
      (Indexable 분리 때 SortedSet/List에서 $getitem 제거가 이 결정)
      - 결정적 구체 예: SortedDict는 키 []를 이미 쓰므로 rank-[]는 모호성 폭탄 —
        `SortedDict[U64, V]`에서 `sd[5]`가 키 5인지 rank 5인지 둘 다 타입이 맞음.
        이름 있는 메서드가 취향이 아니라 유일한 비모호 선택지
    - 비용 정직성 양방향:
        - rank 쿼리는 augmented tree(서브트리 크기를 삽입/삭제마다 유지) 필요 — 다른
          stdlib이 안 주는 이유가 바로 이 비용. RF는 내기로 선택했고 비용을 명시
        - 정면 인정: 멤버십만 보면 SortedSet은 Set을 절대 못 이김 — 해시 O(1) 기대 vs
          트리 O(log n) + 포인터 추적. SortedSet은 "더 좋은 Set"이 아니라 다른 계약:
          Set은 "있나?"에 답하고 SortedSet은 순서에 관한 질문(최소/최대, 범위, 이전/다음,
          rank)에 답함. 존재 이유가 통째로 orderedness — 이름이 곧 사야 할 이유
        - 선택 매트릭스로 정리: 멤버십만 → Set / 순서 쿼리와 변경이 교차 → SortedSet /
          다 넣고 한 번 정렬 → List+sort. (해시 Set으로 순서 보려면 매번 모아서 정렬
          O(n log n) — SortedSet은 그 비용을 삽입마다 O(log n)으로 분할 상환하는 물건)
        - 지는 케이스 인정: 마지막에 한 번만 정렬하면 List+sort 승. sorted 컨테이너의
          승부처는 삽입/삭제와 순서 쿼리가 교차하는 워크로드 — 이 양보가 벤치마크 신뢰성을 만듦
    - 보너스 일관성: get_by_rank!는 failable — 범위 밖 rank에 try_/check_ 변형이 공짜로
      따라옴 → 오류 처리 기계가 stdlib 설계와 합성되는 모습을 조용히 시연
5. 이 언어가 32비트+임베디드 지원을 버리게 된 이유
    - 임베디드 상황의 복잡함
        - heap의 존재성과 entity의 연계
        - heap이 없을 시에 언어가 얼마나 불편해지는지
            - 사실상 동적할당이 어려워짐
              (강화: "어려움"이 아니라 관행적으로 "금지" — MISRA C, NASA/JPL 룰 등 안전규격은
              초기화 이후 동적할당 자체를 금지함. 단편화 + 비결정적 지연 + 복구 불가 OOM 때문.
              인용 가능한 사실이라 논거가 단단해짐)
            - entity의 핵심은 동적할당과 참조를 통한 수정에 있음
              (확장: entity만이 아님 — 단일 소유권/$destroy/RC 티어/클로저/Text·List·Dict 전부
              heap 전제. heap 없는 RazorForge = record + 스택만 남음 = 사실상 다른 언어.
              핵심 문장: "heap이 없으면 entity는 불편해지는 게 아니라 사라진다")
            - 선제 방어: "스코프 기반 결정적 teardown은 오히려 임베디드가 원하는 것 아니냐"
              (RAII 환영론) — 답: RAII는 맞지만 heap + 필수 런타임이 아니어야 함. C/Rust
              no_std/Zig는 처음부터 할당을 명시적·선택적으로 설계했고, RF는 반대 베팅을
              의도적으로 한 것 — 지금 와서 no_std를 붙이는 건 기능 추가가 아니라 코어 재설계
    - 임베디드를 지원을 버리게 되면 32비트는 필요한가?
        - Y2K38 문제
          (주의: Y2K38은 포인터 폭이 아니라 time_t 폭 문제 — 32비트 OS도 64비트 time_t 가능
          (musl, Linux 5.6+). "32비트 생태계에 남은 레거시 관행" 논거로 쓰거나, "RF는 모든
          플랫폼에서 시간이 64비트임을 보장" 형태로 뒤집어 쓸 것)
        - 실제로 많은 OS는 현재 64비트만 지원함 (Win11 64비트 전용, macOS Catalina+,
          Ubuntu i386 중단, Android/iOS 64비트 의무화 — 구체 사례 나열)
    - 64비트 지원 only의 장점
        - 시스템 bit width에 따른 추상화가 필요 없어짐 (usize/size_t류 이식성 골치 제거,
          U64 곧 인덱스 타입; "모든 플랫폼에서 같은 결과" = 언어의 정밀성/결정론 브랜드와 연결)
    - allocator 추상화(Zig식 allocator 파라미터 / Rust custom allocator)를 코어에 안 넣은 이유
        - 타깃이 OS 기반 64비트뿐이면 할당은 OS/런타임 allocator의 일 — 언어가 끼어들 baseline
          명분이 없음 (mimalloc/jemalloc급 현대 allocator는 이미 충분히 강함)
        - 용어 주의: "List에 미리 만들어 둔 것들" = object pool (타입별 재활용). arena는
          bump 할당 + 통째 해제(bulk free)로 다른 물건 — 글에서 혼용하면 바로 지적당함
        - 게임에서 arena를 찾는 use-case 대부분은 object pool로 충족되고, pool은 언어 기능이
          아니라 라이브러리 패턴: List[T] 사전 할당 + free list, 소유권은 pool이 보유,
          대여는 Hijacked (주의: 글 쓰기 전에 실제로 한번 짜서 돌려볼 것 — 성능 주장은 측정으로)
        - 비장의 한 방: 진짜 arena의 가치 = per-object teardown 없이 통째 해제인데, 그건
          RF의 핵심 보장(모든 entity는 정해진 시점에 결정적 $destroy)과 정면 충돌.
          teardown을 돌리면 arena의 성능 이점이 사라지고, 안 돌리면 언어의 보장이 깨짐.
          즉 "아직 안 만든 기능"이 아니라 "중심 보장과 양립 불가라 거른 기능"
        - arena가 entity에 안 되는 건 두 층위 (각각 다른 반박을 막음)
            - 기계적: entity 생성은 런타임 allocator + 생성 경로에 묶인 lifecycle 기계
              ($destroy 삽입, 소유권 추적)라 placement 훅 자체가 없음
            - 의미적: 훅이 있어도 — arena가 통째 reset하면 owner가 dangling (모델이 막는
              바로 그 위험), arena가 소유하게 하면 그건 그냥 owning container (containment =
              ownership이라 지금도 표현 가능)인데 컨테이너 사망 시에도 per-element $destroy는
              돌아감 → arena의 모양만 남고 속도는 안 남음
            - 진짜 arena는 teardown 의무가 없는 데이터에만 성립 → plain-data record의
              List + capacity 예약이 사실상 arena (연속 슬랩, 상환된 할당, 통째 해제,
              per-object 작업 없음) — RF는 이미 안전하게 제공
            - 한 문장 nuance: $destroy 없는 entity는 컴파일러가 teardown no-op을 증명해
              elide → bulk-free가 무해해지는 최적화 여지는 있음. 단 이건 언어 보장 안의
              컴파일러 최적화지, 유저 대면 allocator API가 아님 — "코어는 닫혀 있다" 유지
        - 표현 주의: "AAA 게임급 고성능"류 표현은 positioning 디렉티브 위반 소지 —
          "게임·데이터 도구 같은 성능 민감 애플리케이션"으로 쓰고 성능 주장은 측정치로만
    - 선제 방어 필수: WASM. wasm32가 32비트 포인터라 가장 큰 반론이 될 것 (게다가 온라인
      플레이그라운드 계획과도 얽힘)
        - 결정 (2026-06-12): wasm64가 성숙하면 따라감. wasm32는 지원 안 함 — wasm32를 받는
          순간 이 글이 비판하는 bit-width 이식성 헤징이 언어 안에 되살아나기 때문.
          글에서는 이 일관성 자체를 답으로 쓸 것: "64비트 전용 원칙을 wasm이라고 예외로
          만들지 않는다; 그 예외가 바로 우리가 없앤 비용이다"
        - 부수 효과: 브라우저 내 실행은 당분간 불가 → 플레이그라운드는 서버사이드
          (Compiler Explorer / 샌드박스 VPS)로 간다는 기존 계획과 맞아떨어짐
