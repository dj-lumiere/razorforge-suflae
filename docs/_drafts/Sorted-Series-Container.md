# Why SortedSet/SortedList/SortedDict have its naming, and why not BTree*?

## The reasoning behind naming
- Liskov Substitution Principle (LSP)
  - SortedSet은 Set이 못 가지는 자동 정렬성을 가짐
  - 만약 B-tree를 이용하지 않는 더 나은 새로운 구현이 생긴다면 이름을 변경해야함
- 퍼포먼스
  - SortedSet은 B-tree를 사용하여 자동 정렬성을 제공함
  - 그 자동 정렬성을 유지하기 위해 삽입과 삭제 시 노드 간 이동이 필요함
  - 결과적으로 Set보다 느린 퍼포먼스를 가지게 됨

## 정렬성이 함의하는 것
- 정렬된 컨테이너는 내부적으로 정렬된 데이터를 유지하므로, 그 순서를 가져오는 것이 마땅함
- 특히 그러한 workload는 실시간 중앙값/리더보드 순위/백분위 등에 많이 사용될 수 있음

## 왜 이름 있는 메소드로 지었는가
- RF에서 []는 O(1)급 비용 신호. rank 접근은 의미도 다르고 비용도 O(log n)이라
  이름을 줌 → 호출부가 비용을 말해주는 API (cost honesty)
