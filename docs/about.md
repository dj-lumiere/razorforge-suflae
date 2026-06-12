---
layout: page
title: 소개
permalink: /about/
---

[RazorForge](https://github.com/dj-lumiere/razorforge-suflae)를 만들면서 남기는 개발 블로그입니다.

RazorForge는 두 가지 아이디어를 중심으로 설계한 네이티브 컴파일 정적 타입 언어입니다.

1. **빌림 검사기(borrow checker) 없는 단일 소유권 메모리 관리** — 담고 있으면 소유한 것이고,
   소유권 이전은 `steal`로 명시하며, 빌림은 스코프에 묶입니다.
2. **컴파일러가 만들어 주는 오류 처리** — 실패 가능 루틴(`routine parse!(...)`) 하나를 쓰면
   컴파일러가 `try_`/`check_`/`lookup_` 변형을 호출 방식별로 만들어 줍니다.

이 블로그에는 언어를 설계하면서 내린 결정들과, 그걸 구현하는 데 실제로 무엇이 필요했는지를
기록합니다.